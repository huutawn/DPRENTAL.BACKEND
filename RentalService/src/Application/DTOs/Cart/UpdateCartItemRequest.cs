namespace Dprental.RentalService.Api.Contracts.Cart;

public sealed record UpdateCartItemRequest(
    int? Quantity,
    DateOnly? ExpectedStartDate,
    DateOnly? ExpectedEndDate);
