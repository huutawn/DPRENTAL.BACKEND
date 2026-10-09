using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dprental.RentalService.Application.Cart.ReadModel;

public interface IRentalAvailabilityReader
{
    Task<int?> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}