using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
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

        // 2. Kiểm tra trùng lặp với các khoảng chặn lịch (Calendar Blocks)
        foreach (var block in existingBlocks)
        {
            if (candidatePeriod.Overlaps(block.StartDate, block.EndDate))
            {
                return Error.Conflict("ASSET_BLOCKED", $"Tài sản đã bị chặn lịch từ ngày {block.StartDate:yyyy-MM-dd} đến {block.EndDate:yyyy-MM-dd} (Lý do: {block.Reason}).");
            }
        }

        // 3. Kiểm tra trùng lặp với các lượt phân bổ hiện có (Allocations)
        foreach (var alloc in existingAllocations)
        {
            if (alloc.Id == excludeAllocationId)
            {
                continue;
            }

            // A. Lượt giữ chỗ (Held) đã quá hạn tại thời điểm kiểm tra: không chiếm lịch
            if (alloc.Status == AllocationStatus.Held)
            {
                if (alloc.HoldExpiresAt.HasValue && now >= alloc.HoldExpiresAt.Value)
                {
                    continue;
                }
            }

            // B. Trạng thái kết thúc không chiếm lịch: Released, Cancelled
            if (alloc.Status is AllocationStatus.Released or AllocationStatus.Cancelled)
            {
                continue;
            }

            // C. Các trạng thái đang chiếm lịch: Held (còn hạn) và Allocated
            if (candidatePeriod.Overlaps(alloc.RentalPeriod))
            {
                return Error.Conflict("ASSET_UNAVAILABLE", $"Tài sản đã có lượt phân bổ ({alloc.ExternalReference}, trạng thái: {alloc.Status}) trùng với khoảng thời gian từ {alloc.RentalPeriod.StartDate:yyyy-MM-dd} đến {alloc.RentalPeriod.EndDate:yyyy-MM-dd}.");
            }
        }

        return Result.Success();
    }
}
