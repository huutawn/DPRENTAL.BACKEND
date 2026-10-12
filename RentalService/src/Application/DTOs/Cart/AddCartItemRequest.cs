namespace Dprental.RentalService.Api.Contracts.Cart;

public sealed record AddCartItemRequest(
    Guid EquipmentListingId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate);
