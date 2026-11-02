
namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// Kết quả kiểm tra tính khả dụng của một EquipmentListing.
/// </summary>
public sealed record CheckListingAvailabilityResponse(
    Guid ListingId,
    DateOnly StartDate,
    DateOnly EndDate,
    int RequestedQuantity,
    int AvailableQuantity,
    bool IsAvailable
);
