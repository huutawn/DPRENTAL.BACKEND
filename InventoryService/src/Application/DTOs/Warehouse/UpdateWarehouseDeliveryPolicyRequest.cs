namespace Dprental.InventoryService.Application.DTOs.Warehouse;

public sealed record UpdateWarehouseDeliveryPolicyRequest(
    bool IsPickupEnabled,
    OperatingHoursDto OperatingHours,
    IReadOnlyList<DeliveryZoneDto> DeliveryZones,
    uint? ExpectedVersion = null);
