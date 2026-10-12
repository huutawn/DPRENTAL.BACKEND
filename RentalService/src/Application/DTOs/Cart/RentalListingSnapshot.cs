namespace Dprental.RentalService.Application.DTOs.Cart;

public sealed record RentalListingSnapshot(
    Guid ListingId,
    Guid OwnerId,
    bool IsPublished,
    bool AcceptsBookings,
    decimal DailyRate,
    decimal Deposit,
    string? PriceVersion);
