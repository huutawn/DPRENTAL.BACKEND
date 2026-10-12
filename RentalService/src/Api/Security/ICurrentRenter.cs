using System;

namespace Dprental.RentalService.Api.Security;

public interface ICurrentRenter
{
    Guid RenterId { get; }
}