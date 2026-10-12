namespace Dprental.RentalService.Application.Clients;

public interface IRentalListingOwnerProvider
{
    Task<Guid?> GetOwnerIdAsync(
        Guid equipmentListingId,
        CancellationToken cancellationToken = default);
}