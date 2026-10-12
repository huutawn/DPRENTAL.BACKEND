namespace Dprental.RentalService.Application.DTOs.Cart;

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
