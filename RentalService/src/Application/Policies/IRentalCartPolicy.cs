namespace Dprental.RentalService.Application.Policies;

public interface IRentalCartPolicy
{
    int MaximumRentalDays { get; }
}
