using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Calendar;

/// <summary>
/// Thống kê chi tiết một ngày trong lịch dùng chung của EquipmentListing.
/// </summary>
public sealed record ListingCalendarDayResponse(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("occupiedQuantity")] int OccupiedQuantity,
    [property: JsonPropertyName("blockedQuantity")] int BlockedQuantity,
    [property: JsonPropertyName("availableQuantity")] int AvailableQuantity,
    [property: JsonPropertyName("suspendedOccupiedQuantity")] int SuspendedOccupiedQuantity,
    [property: JsonPropertyName("suspendedBlockedQuantity")] int SuspendedBlockedQuantity
);

/// <summary>
/// Lịch tổng hợp dùng chung cấp EquipmentListing cho chủ sở hữu hoặc quản trị viên.
/// </summary>
public sealed record ListingCalendarResponse(
    [property: JsonPropertyName("listingId")] Guid ListingId,
    [property: JsonPropertyName("from")] DateOnly From,
    [property: JsonPropertyName("to")] DateOnly To,
    [property: JsonPropertyName("totalQuantity")] int TotalQuantity,
    [property: JsonPropertyName("days")] IReadOnlyList<ListingCalendarDayResponse> Days
);
