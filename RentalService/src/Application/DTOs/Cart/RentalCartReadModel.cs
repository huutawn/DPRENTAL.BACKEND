using System;
using System.Collections.Generic;

namespace Dprental.RentalService.Application.DTOs.Cart;



public sealed record RentalCartReadModel(
    Guid CartId,
    IReadOnlyList<RentalCartLineReadModel> Items);