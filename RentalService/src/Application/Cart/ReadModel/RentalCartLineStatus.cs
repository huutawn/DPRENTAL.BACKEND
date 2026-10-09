namespace Dprental.RentalService.Application.Cart.ReadModel;

public enum RentalCartLineStatus
{
    Available,
    PriceChanged,
    InsufficientQuantity,
    Unavailable,
    ListingDisabled,
    ValidationFailed
}