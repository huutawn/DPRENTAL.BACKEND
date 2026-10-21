namespace Dprental.RecommendationService.Domain.IRepositories;

public enum PublishOutcome
{
    Published,
    Busy,
    LeaseLost,
    Unavailable,
    Failed
}

public enum LeaseAcquireStatus
{
    Acquired,
    Busy,
    Unavailable,
    Failed
}

public sealed record RankingLease(string DataKey, string LeaseKey, string Token);

public sealed record LeaseAcquireResult(
    LeaseAcquireStatus Status,
    RankingLease? Lease = null,
    string? Reason = null);

public interface IValkeyProjectionRepository
{
    Task<LeaseAcquireResult> TryAcquireUserLeaseAsync(Guid userId, CancellationToken cancellationToken);

    Task<LeaseAcquireResult> TryAcquireGlobalLeaseAsync(CancellationToken cancellationToken);

    Task<PublishOutcome> ReplaceUserRankingAsync(
        RankingLease lease,
        IReadOnlyList<UserEquipmentTypeInterestItem> items,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    Task<PublishOutcome> ReplaceGlobalRankingAsync(
        RankingLease lease,
        IReadOnlyList<ListingPopularityItem> items,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    Task<bool> ReleaseLeaseAsync(RankingLease lease, CancellationToken cancellationToken);
}
