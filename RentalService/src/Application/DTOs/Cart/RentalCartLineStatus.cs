namespace Dprental.RentalService.Application.DTOs.Cart;

public enum RentalCartLineStatus
{
    Available,
    PriceChanged,
    InsufficientQuantity,
    Unavailable,
    ListingDisabled,
    ValidationFailed,
    DatePassed,
}