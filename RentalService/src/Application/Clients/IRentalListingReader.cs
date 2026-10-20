using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

public interface IRentalListingReader
{
    Task<Result<RentalListingSnapshot>> GetListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);
}
