using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Domain.Policies;

/// <summary>
/// Chính sách miền nghiệp vụ dùng chung kiểm tra tính khả dụng của thiết bị (Shared Domain Availability Policy).
/// Đóng gói toàn bộ quy tắc về điều kiện nhận đặt, kiểm tra chân trời nghiệp vụ, xử lý các hold quá hạn,
/// ngày bận thu hồi D của lượt Completed và tình trạng thiết bị quá hạn chưa hoàn trả (overdue).
/// </summary>
public static class EquipmentAvailabilityPolicy
{
    /// <summary>
    /// Kiểm tra tính khả dụng của thiết bị đối với một khoảng thời gian thuê đề xuất.
    /// </summary>
    /// <param name="equipment">Đối tượng thiết bị cần kiểm tra.</param>
    /// <param name="candidatePeriod">Khoảng thời gian thuê đề xuất.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="existingReservations">Danh sách các reservation ứng viên hiện có trên thiết bị.</param>
    /// <param name="existingBlocks">Danh sách các khoảng chặn lịch hiện có trên thiết bị.</param>
    /// <param name="excludeReservationId">ID của reservation cần loại trừ nếu là thao tác cập nhật chính nó.</param>
    /// <returns>Result thành công nếu thiết bị khả dụng, ngược lại trả về lỗi cụ thể.</returns>
    public static Result CheckAvailability(
        InventoryEquipment equipment,
        RentalPeriod candidatePeriod,
        DateTimeOffset now,
        IEnumerable<Reservation> existingReservations,
        IEnumerable<CalendarBlock> existingBlocks,
        Guid? excludeReservationId = null)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(candidatePeriod);
        ArgumentNullException.ThrowIfNull(existingReservations);
        ArgumentNullException.ThrowIfNull(existingBlocks);

        // 1. Kiểm tra thiết bị có đang mở nhận đặt lịch và có trạng thái Catalog là Active không
        if (!equipment.CanAcceptBookings())
        {
            return Error.Validation("EQUIPMENT_NOT_ACCEPTING_BOOKINGS", "Thiết bị hiện tại không mở nhận đặt lịch hoặc trạng thái Catalog không phải Active.");
        }

        // 2. Kiểm tra chân trời đặt lịch (ngày bắt đầu không trước hôm nay, kết thúc không quá 6 tháng)
        DateOnly today = BusinessDateTime.ToVietnamDate(now);
        var horizonCheck = candidatePeriod.ValidateBusinessHorizon(today);
        if (horizonCheck.IsFailure)
        {
            return horizonCheck.Error;
        }

        // 3. Kiểm tra tình trạng quá hạn trả (Overdue):
        // Nếu thiết bị có reservation đang InUse mà EndDate < today (quá hạn trả chưa thu hồi),
        // thì không cho phép đặt lịch mới bắt đầu vào hôm nay hoặc ngày mai (StartDate <= today + 1 ngày).
        bool hasOverdueInUse = existingReservations.Any(r =>
            r.Id != excludeReservationId &&
            r.Status == ReservationStatus.InUse &&
            r.RentalPeriod.EndDate < today);

        if (hasOverdueInUse && candidatePeriod.StartDate <= today.AddDays(1))
        {
            return Error.Conflict("EQUIPMENT_OVERDUE_UNAVAILABLE", "Thiết bị đang trong tình trạng quá hạn chưa thu hồi, không thể nhận lượt đặt bắt đầu vào hôm nay hoặc ngày mai.");
        }

        // 4. Kiểm tra trùng lặp với các khoảng chặn lịch (Calendar Blocks)
        foreach (var block in existingBlocks)
        {
            if (candidatePeriod.Overlaps(block.StartDate, block.EndDate))
            {
                return Error.Conflict("EQUIPMENT_BLOCKED", $"Thiết bị đã bị chặn lịch từ ngày {block.StartDate:yyyy-MM-dd} đến {block.EndDate:yyyy-MM-dd} (Lý do: {block.Reason}).");
            }
        }

        // 5. Kiểm tra trùng lặp với các lượt đặt lịch hiện có (Reservations)
        foreach (var res in existingReservations)
        {
            if (res.Id == excludeReservationId)
            {
                continue;
            }

            // A. Lượt giữ chỗ (Held) đã quá hạn tại thời điểm kiểm tra: không chiếm lịch
            if (res.Status == ReservationStatus.Held)
            {
                if (res.HoldExpiresAt.HasValue && now >= res.HoldExpiresAt.Value)
                {
                    continue;
                }
            }

            // B. Trạng thái kết thúc không chiếm lịch: Released, Cancelled
            if (res.Status is ReservationStatus.Released or ReservationStatus.Cancelled)
            {
                continue;
            }

            // C. Trạng thái Completed:
            // Theo quy tắc nghiệp vụ: Thu hồi ngày D cho đặt lại từ D+1. Availability phải giữ ngày D là ngày bận dù reservation đã Completed.
            if (res.Status == ReservationStatus.Completed)
            {
                // Nếu khoảng đề xuất giao với khoảng thời gian đã thuê (bao gồm cả ngày kết thúc D):
                if (res.ReturnedAt is not null && candidatePeriod.Overlaps(
                    DateOnly.FromDayNumber(Math.Min(res.RentalPeriod.StartDate.DayNumber, BusinessDateTime.ToVietnamDate(res.ReturnedAt.Value).DayNumber)),
                    BusinessDateTime.ToVietnamDate(res.ReturnedAt.Value)))
                {
                    return Error.Conflict("EQUIPMENT_UNAVAILABLE", $"Thiết bị bận do lượt đặt {res.ReservationCode} vừa hoàn tất và ngày thu hồi ({BusinessDateTime.ToVietnamDate(res.ReturnedAt!.Value):yyyy-MM-dd}) vẫn giữ là ngày bận.");
                }
                continue;
            }

            // D. Các trạng thái đang chiếm lịch bình thường: Held (còn hạn), Confirmed, InUse
            if (candidatePeriod.Overlaps(res.RentalPeriod))
            {
                return Error.Conflict("EQUIPMENT_UNAVAILABLE", $"Thiết bị đã có lượt đặt ({res.ReservationCode}, trạng thái: {res.Status}) trùng với khoảng thời gian từ {res.RentalPeriod.StartDate:yyyy-MM-dd} đến {res.RentalPeriod.EndDate:yyyy-MM-dd}.");
            }
        }

        return Result.Success();
    }
}
