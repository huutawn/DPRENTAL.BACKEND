
namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin kho lưu trữ đi kèm trong API batch EquipmentListing phục vụ Rental checkout.
/// </summary>
public sealed record EquipmentListingBatchWarehouseDto(
    string? AddressDetail,
    string? ProvinceCode,
    string? ProvinceName,
    string? WardCode,
    string? WardName,
    double? Latitude,
    double? Longitude
);
