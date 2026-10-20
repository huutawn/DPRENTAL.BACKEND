using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Kết quả đọc danh sách EquipmentListing theo lô (batch) phục vụ Rental Service.
/// </summary>
public sealed record EquipmentListingBatchResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<EquipmentListingBatchItemResponse> Items,
    [property: JsonPropertyName("missingIds")] IReadOnlyList<Guid> MissingIds
);
