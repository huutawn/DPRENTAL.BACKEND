namespace Dprental.InventoryService.Application.DTOs.Warehouse;

public sealed record WarehouseAddressDto(
    string? AddressDetail,
    string? ProvinceCode,
    string? WardCode);
