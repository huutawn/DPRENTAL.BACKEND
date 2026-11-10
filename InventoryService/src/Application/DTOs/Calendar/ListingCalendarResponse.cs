
namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// Thống kê chi tiết một ngày trong lịch dùng chung của EquipmentListing.
/// </summary>
public sealed record ListingCalendarDayResponse(
    DateOnly Date,
    int OccupiedQuantity,
    int BlockedQuantity,
    int AvailableQuantity,
    int SuspendedOccupiedQuantity,
    int SuspendedBlockedQuantity
);

/// <summary>
/// Lịch tổng hợp dùng chung cấp EquipmentListing cho chủ sở hữu hoặc quản trị viên.
/// </summary>
public sealed record ListingCalendarResponse(
    Guid ListingId,
    DateOnly From,
    DateOnly To,
    int TotalQuantity,
    IReadOnlyList<ListingCalendarDayResponse> Days
);
