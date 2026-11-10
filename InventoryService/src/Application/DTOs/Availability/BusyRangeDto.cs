
namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Khoảng ngày bận của tài sản (inclusive).
/// </summary>
public sealed record BusyRangeDto(
    DateOnly StartDate,
    DateOnly EndDate
);
