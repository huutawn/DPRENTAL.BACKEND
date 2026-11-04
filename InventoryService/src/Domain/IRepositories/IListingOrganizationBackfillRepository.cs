namespace Dprental.InventoryService.Domain.IRepositories;

public sealed record ListingOrganizationCandidate(Guid ListingId, Guid OwnerUserId);

public interface IListingOrganizationBackfillRepository
{
    Task<IReadOnlyList<ListingOrganizationCandidate>> GetMissingAsync(Guid? afterId, int limit, CancellationToken cancellationToken);
}
