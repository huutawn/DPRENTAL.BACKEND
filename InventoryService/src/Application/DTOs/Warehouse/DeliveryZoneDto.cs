namespace Dprental.InventoryService.Application.DTOs.Warehouse;

public sealed record DeliveryZoneDto(
    string ProvinceCode,
    string? WardCode,
    decimal DeliveryFeeVnd,
    decimal ReturnFeeVnd);
