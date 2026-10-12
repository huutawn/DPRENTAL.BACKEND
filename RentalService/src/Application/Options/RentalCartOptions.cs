using Dprental.RentalService.Application.Policies;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.RentalService.Application.Options
{
    public sealed class RentalCartOptions : IRentalCartPolicy
    {
        public int MaximumRentalDays { get; init; } = Dprental.RentalService.Domain.Entities.RentalCart.MaximumRentalDays;
    }
}
