namespace Dprental.InventoryService.Application.Clients;

public interface ISupplierOrganizationLookup
{
    Task<Guid?> FindByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
