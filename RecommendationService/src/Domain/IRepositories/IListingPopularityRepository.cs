namespace Dprental.RecommendationService.Domain.IRepositories;

public sealed record ListingPopularityItem(Guid EquipmentListingId, long Score);

public interface IListingPopularityRepository
{
    Task UpsertAsync(Guid equipmentListingId, short scoreWeight, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<ListingPopularityItem>> GetTopPopularListingsAsync(int limit, CancellationToken cancellationToken);
}
