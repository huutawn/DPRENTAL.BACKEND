using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Kết quả kiểm tra tính khả dụng của một InventoryAsset.
/// </summary>
public sealed record CheckAssetAvailabilityResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("requestedQuantity")] int RequestedQuantity,
    [property: JsonPropertyName("availableQuantity")] int AvailableQuantity,
    [property: JsonPropertyName("isAvailable")] bool IsAvailable
);
