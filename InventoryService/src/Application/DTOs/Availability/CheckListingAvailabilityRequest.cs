
namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Yêu cầu kiểm tra tính khả dụng của một EquipmentListing trong khoảng thời gian đề xuất.
/// </summary>
public sealed record CheckListingAvailabilityRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    int RequestedQuantity = 1
);
