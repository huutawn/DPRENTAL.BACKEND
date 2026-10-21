namespace Dprental.InventoryService.Application.Caching;

public sealed record RecommendationSnapshot(
    IReadOnlyList<Guid> RankedIds,
    DateTimeOffset GeneratedAtUtc,
    string ContentHash);

public interface IRecommendationSnapshotReader
{
    Task<RecommendationSnapshot?> GetPersonalTypesAsync(Guid userId, CancellationToken cancellationToken);
    Task<RecommendationSnapshot?> GetGlobalListingsAsync(CancellationToken cancellationToken);
}
