using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Yêu cầu kiểm tra tính khả dụng của một EquipmentListing trong khoảng thời gian đề xuất.
/// </summary>
public sealed record CheckListingAvailabilityRequest(
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("requestedQuantity")] int RequestedQuantity = 1
);
