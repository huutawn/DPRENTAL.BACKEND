using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dprental.RentalService.Application.Cart.ReadModel;

public sealed record RentalListingSnapshot(
    Guid ListingId,
    Guid OwnerId,
    bool IsPublished,
    bool AcceptsBookings,
    decimal DailyRate,
    decimal Deposit,
    string? PriceVersion);

public interface IRentalListingReader
{
    Task<RentalListingSnapshot?> GetListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);
}