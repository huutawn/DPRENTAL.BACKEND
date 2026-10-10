using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Projection record phục vụ truy vấn lịch thuê cá nhân của người thuê.
/// </summary>
public sealed record RenterCalendarItemRecord(
    Guid OrderId,
    string OrderCode,
    Guid RentalItemId,
    Guid EquipmentListingId,
    string ListingTitle,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    RentalItemStatus Status,
    DateTimeOffset CreatedAt);
