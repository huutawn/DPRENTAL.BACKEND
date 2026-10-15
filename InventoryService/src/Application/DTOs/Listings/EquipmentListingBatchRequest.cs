using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu đọc danh sách thông tin EquipmentListing theo lô (batch) phục vụ Rental Service.
/// Giới hạn từ 1 đến 100 ID.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentListingBatchRequest(
    [property: JsonPropertyName("equipmentListingIds")] IReadOnlyList<Guid>? EquipmentListingIds
);
