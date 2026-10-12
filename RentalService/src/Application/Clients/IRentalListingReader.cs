using Dprental.RentalService.Application.DTOs.Cart;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dprental.RentalService.Application.Clients;



public interface IRentalListingReader
{
    Task<RentalListingSnapshot?> GetListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);
}