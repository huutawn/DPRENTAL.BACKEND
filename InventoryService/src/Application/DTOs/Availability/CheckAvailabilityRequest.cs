using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Yêu cầu chung kiểm tra tính khả dụng của EquipmentListing hoặc InventoryAsset trong khoảng thời gian đề xuất.
/// </summary>
public sealed record CheckAvailabilityRequest(
    [property: JsonPropertyName("startDate")] DateOnly? StartDate = null,
    [property: JsonPropertyName("endDate")] DateOnly? EndDate = null,
    [property: JsonPropertyName("requestedQuantity")] int RequestedQuantity = 1
);
