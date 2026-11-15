namespace Dprental.InventoryService.Application.DTOs.Warehouse;

public sealed record WarehouseDeliveryPolicyResponse(
    Guid SupplierId,
    bool IsPickupEnabled,
    WarehouseAddressDto? WarehouseAddress,
    OperatingHoursDto OperatingHours,
    IReadOnlyList<DeliveryZoneDto> DeliveryZones);
