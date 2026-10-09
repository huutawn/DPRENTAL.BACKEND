namespace Dprental.RentalService.Application.Cart;

public interface IRentalListingOwnerProvider
{
    Task<Guid?> GetOwnerIdAsync(
        Guid equipmentListingId,
        CancellationToken cancellationToken = default);
}