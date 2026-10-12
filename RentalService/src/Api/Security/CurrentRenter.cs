using System;
using Dprental.RentalService.Application.Security;

namespace Dprental.RentalService.Api.Security;

public sealed class CurrentRenter(IRentalCallerContext callerContext) : ICurrentRenter
{
    public Guid RenterId
    {
        get
        {
            if (callerContext.IsAuthenticated && !callerContext.IsService &&
                callerContext.UserId is Guid renterId && renterId != Guid.Empty)
            {
                return renterId;
            }

            throw new InvalidOperationException("The authenticated renter token does not contain a valid sub claim.");
        }
    }
}
