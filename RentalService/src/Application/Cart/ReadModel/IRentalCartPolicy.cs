namespace Dprental.RentalService.Application.Cart.ReadModel;

public interface IRentalCartPolicy
{
    int MaximumRentalDays { get; }
}
