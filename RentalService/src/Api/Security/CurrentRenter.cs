using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Dprental.RentalService.Api.Security;

public sealed class CurrentRenter(IHttpContextAccessor httpContextAccessor) : ICurrentRenter
{
    public Guid RenterId
    {
        get
        {
            string? subject = httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
            if (Guid.TryParse(subject, out Guid renterId) && renterId != Guid.Empty)
            {
                return renterId;
            }

            throw new InvalidOperationException("The authenticated renter token does not contain a valid sub claim.");
        }
    }
}
