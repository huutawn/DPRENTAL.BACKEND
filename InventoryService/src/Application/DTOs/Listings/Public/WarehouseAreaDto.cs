
namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin khu vực địa lý công khai của kho cho thuê (chỉ gồm Tỉnh/Thành và Phường/Xã).
/// Tuyệt đối không để lộ địa chỉ chi tiết hoặc tọa độ GPS chính xác ra bên ngoài.
/// </summary>
public sealed record WarehouseAreaDto(
    string? ProvinceCode,
    string? ProvinceName,
    string? WardCode,
    string? WardName
);
