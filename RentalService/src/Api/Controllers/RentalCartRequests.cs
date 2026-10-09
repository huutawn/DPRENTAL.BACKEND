using System;

namespace Dprental.RentalService.Api.Controllers;

public sealed record AddCartItemRequest(
    Guid EquipmentListingId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate);

public sealed record UpdateCartItemRequest(
    int? Quantity,
    DateOnly? ExpectedStartDate,
    DateOnly? ExpectedEndDate);