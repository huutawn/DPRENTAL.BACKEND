namespace Dprental.RentalService.Application.DTOs.Cart;

public sealed record RentalListingSnapshot(
    Guid ListingId,
    Guid OwnerId,
    Guid EquipmentTypeId,
    bool IsPublished,
    bool AcceptsBookings,
    decimal DailyRate,
    decimal Deposit,
    string? PriceVersion)
{
    public RentalListingSnapshot(
        Guid listingId,
        Guid ownerId,
        bool isPublished,
        bool acceptsBookings,
        decimal dailyRate,
        decimal deposit,
        string? priceVersion)
        : this(listingId, ownerId, Guid.Empty, isPublished, acceptsBookings, dailyRate, deposit, priceVersion)
    {
    }
}
