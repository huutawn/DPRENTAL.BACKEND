using System;
using System.Collections.Generic;

namespace Dprental.RentalService.Application.Cart.ReadModel;

public sealed record RentalCartLineReadModel(
    Guid CartItemId,
    Guid EquipmentListingId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    decimal? CurrentDailyRate,
    decimal? CurrentDeposit,
    decimal? EstimatedRentalAmount,
    string? CurrentPriceVersion,
    RentalCartLineStatus Status,
    string? Message);

public sealed record RentalCartReadModel(
    Guid CartId,
    IReadOnlyList<RentalCartLineReadModel> Items);