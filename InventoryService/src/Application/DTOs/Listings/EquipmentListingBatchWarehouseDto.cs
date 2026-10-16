using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin kho lưu trữ đi kèm trong API batch EquipmentListing phục vụ Rental checkout.
/// </summary>
public sealed record EquipmentListingBatchWarehouseDto(
    [property: JsonPropertyName("addressDetail")] string? AddressDetail,
    [property: JsonPropertyName("provinceCode")] string? ProvinceCode,
    [property: JsonPropertyName("provinceName")] string? ProvinceName,
    [property: JsonPropertyName("wardCode")] string? WardCode,
    [property: JsonPropertyName("wardName")] string? WardName,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude
);
