using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Domain.Policies;

/// <summary>
/// Chính sách miền nghiệp vụ kiểm tra tính khả dụng của tài sản (Asset Availability Policy).
/// Kiểm tra công tắc nhận đặt lịch, các khoảng chặn lịch (Calendar Blocks), và các lượt phân bổ đang chiếm lịch (Held còn hạn, Allocated).
/// </summary>
public static class AssetAvailabilityPolicy
{
    /// <summary>
    /// Kiểm tra tính khả dụng của tài sản đối với một khoảng thời gian đề xuất.
    /// </summary>
    /// <param name="asset">Đối tượng tài sản cần kiểm tra.</param>
    /// <param name="candidatePeriod">Khoảng thời gian đề xuất.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="existingAllocations">Danh sách các phân bổ ứng viên hiện có trên tài sản.</param>
    /// <param name="existingBlocks">Danh sách các khoảng chặn lịch hiện có trên tài sản.</param>
    /// <param name="excludeAllocationId">ID của phân bổ cần loại trừ nếu là thao tác cập nhật chính nó.</param>
    /// <returns>Result thành công nếu tài sản khả dụng, ngược lại trả về lỗi cụ thể.</returns>
    public static Result CheckAvailability(
        InventoryAsset asset,
        RentalPeriod candidatePeriod,
        DateTimeOffset now,
        IEnumerable<AssetAllocation> existingAllocations,
        IEnumerable<CalendarBlock> existingBlocks,
        Guid? excludeAllocationId = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(candidatePeriod);
        ArgumentNullException.ThrowIfNull(existingAllocations);
        ArgumentNullException.ThrowIfNull(existingBlocks);

        // 1. Kiểm tra tài sản có đang mở nhận đặt lịch hay không
        if (!asset.CanAcceptBookings())
        {
            return Error.Validation("ASSET_NOT_ACCEPTING_BOOKINGS", "Tài sản hiện tại không mở nhận đặt lịch.");
        }

        // 2. Kiểm tra trùng lặp lịch dùng chung qua CheckCalendarOverlap
        return CheckCalendarOverlap(
            candidatePeriod.StartDate,
            candidatePeriod.EndDate,
            now,
            existingAllocations,
            existingBlocks,
            excludeAllocationId: excludeAllocationId);
    }

    /// <summary>
    /// Kiểm tra trùng lặp với các khoảng chặn lịch và các lượt phân bổ đang chiếm lịch trên tài sản.
    /// Không kiểm tra công tắc AcceptingBookings (phục vụ cả việc tạo CalendarBlock khi tài sản tắt booking).
    /// </summary>
    /// <param name="startDate">Ngày bắt đầu cần kiểm tra (inclusive).</param>
    /// <param name="endDate">Ngày kết thúc cần kiểm tra (inclusive).</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="existingAllocations">Danh sách các phân bổ hiện có.</param>
    /// <param name="existingBlocks">Danh sách các khoảng chặn lịch hiện có.</param>
    /// <param name="excludeAllocationId">ID phân bổ cần loại trừ (nếu có).</param>
    /// <returns>Result thành công nếu không bị overlap, ngược lại trả về Conflict lỗi tương ứng.</returns>
    public static Result CheckCalendarOverlap(
        DateOnly startDate,
        DateOnly endDate,
        DateTimeOffset now,
        IEnumerable<AssetAllocation> existingAllocations,
        IEnumerable<CalendarBlock> existingBlocks,
        Guid? excludeAllocationId = null)
    {
        ArgumentNullException.ThrowIfNull(existingAllocations);
        ArgumentNullException.ThrowIfNull(existingBlocks);

        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATE_ORDER", "Ngày bắt đầu không thể sau ngày kết thúc.");
        }

        // 1. Kiểm tra trùng lặp với các khoảng chặn lịch (Calendar Blocks)
        foreach (var block in existingBlocks)
        {
            if (block.Overlaps(startDate, endDate))
            {
                return Error.Conflict("ASSET_BLOCKED", $"Tài sản đã bị chặn lịch từ ngày {block.StartDate:yyyy-MM-dd} đến {block.EndDate:yyyy-MM-dd} (Lý do: {block.Reason}).");
            }
        }

        // 2. Kiểm tra trùng lặp với các lượt phân bổ hiện có (Allocations)
        foreach (var alloc in existingAllocations)
        {
            if (excludeAllocationId.HasValue && alloc.Id == excludeAllocationId.Value)
            {
                continue;
            }

            var interval = GetOccupiedInterval(alloc, now);
            if (!interval.HasValue)
            {
                continue;
            }

            var (occupiedStart, occupiedEnd) = interval.Value;
            if (occupiedStart <= endDate && occupiedEnd >= startDate)
            {
                string desc = occupiedEnd == DateOnly.MaxValue
                    ? $"từ ngày {occupiedStart:yyyy-MM-dd} (đã bàn giao, quá hạn chưa trả)"
                    : $"từ {occupiedStart:yyyy-MM-dd} đến {occupiedEnd:yyyy-MM-dd}";

                return Error.Conflict("ASSET_UNAVAILABLE", $"Tài sản đã có lượt phân bổ ({alloc.ExternalReference}, trạng thái: {alloc.Status}) trùng với khoảng thời gian {desc}.");
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Xác định khoảng chiếm dụng thực tế của một lượt phân bổ.
    /// Trả về null nếu phân bổ không chiếm lịch tại thời điểm kiểm tra.
    /// Nếu máy đã bàn giao nhưng chưa trả và ngày hiện tại tại VN đã vượt EndDate, chiếm dụng kéo dài đến DateOnly.MaxValue.
    /// </summary>
    public static (DateOnly StartDate, DateOnly EndDate)? GetOccupiedInterval(AssetAllocation allocation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        switch (allocation.Status)
        {
            case AllocationStatus.Released:
            case AllocationStatus.Cancelled:
                return null;

            case AllocationStatus.Held:
                if (!allocation.HoldExpiresAt.HasValue || allocation.HoldExpiresAt.Value <= now)
                {
                    return null;
                }
                return (allocation.RentalPeriod.StartDate, allocation.RentalPeriod.EndDate);

            case AllocationStatus.Allocated:
                var todayVN = BusinessDateTime.ToVietnamDate(now);
                if (allocation.HandedOverAt.HasValue && !allocation.ReturnedAt.HasValue && todayVN > allocation.RentalPeriod.EndDate)
                {
                    return (allocation.RentalPeriod.StartDate, DateOnly.MaxValue);
                }
                return (allocation.RentalPeriod.StartDate, allocation.RentalPeriod.EndDate);

            default:
                return null;
        }
    }

    /// <summary>
    /// Lấy giao của khoảng chiếm dụng với một cửa sổ hiển thị [windowStart, windowEnd].
    /// Trả về null nếu không có giao điểm.
    /// </summary>
    public static (DateOnly StartDate, DateOnly EndDate)? GetDisplayOccupiedInterval(
        AssetAllocation allocation,
        DateOnly windowStart,
        DateOnly windowEnd,
        DateTimeOffset now)
    {
        var interval = GetOccupiedInterval(allocation, now);
        if (!interval.HasValue)
        {
            return null;
        }

        var (start, end) = interval.Value;
        if (start > windowEnd || end < windowStart)
        {
            return null;
        }

        var displayStart = start < windowStart ? windowStart : start;
        var displayEnd = end > windowEnd ? windowEnd : end;

        return (displayStart, displayEnd);
    }

    /// <summary>
    /// Predicate dùng chung xác định một lượt phân bổ có đang chiếm lịch tại thời điểm kiểm tra hay không.
    /// Dùng cho cả việc kiểm tra overlap và đọc lịch tổng hợp (Calendar Read).
    /// </summary>
    /// <param name="allocation">Lượt phân bổ cần kiểm tra.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <returns>True nếu phân bổ đang chiếm lịch; ngược lại False.</returns>
    public static bool OccupiesCalendar(AssetAllocation allocation, DateTimeOffset now) =>
        GetOccupiedInterval(allocation, now).HasValue;
}
