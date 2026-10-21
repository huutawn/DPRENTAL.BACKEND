using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

public interface IRentalAvailabilityReader
{
    Task<Result<int>> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}
