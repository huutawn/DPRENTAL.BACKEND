namespace Dprental.InventoryService.Application.DTOs.Warehouse;

public sealed record OperatingHoursDto(
    string OpenTime,
    string CloseTime,
    IReadOnlyList<int> ClosedDays);
