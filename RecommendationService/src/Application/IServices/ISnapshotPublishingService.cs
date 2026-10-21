using Dprental.RecommendationService.Domain.IRepositories;

namespace Dprental.RecommendationService.Application.IServices;

public sealed record PublishResult(PublishOutcome Outcome, string? Reason = null);

public sealed record RebuildErrorDetail(string Target, string ErrorCode, string Message);

public sealed record RebuildCacheResult(
    int TotalEvaluated,
    int PublishedCount,
    int BusyCount,
    int LeaseLostCount,
    int FailedCount,
    IReadOnlyList<RebuildErrorDetail> Errors)
{
    public int SkippedCount => BusyCount + LeaseLostCount;

    public bool IsSuccessful =>
        Errors.Count == 0 &&
        FailedCount == 0 &&
        BusyCount == 0 &&
        LeaseLostCount == 0 &&
        PublishedCount == TotalEvaluated;
}

public interface ISnapshotPublishingService
{
    Task<PublishResult> PublishUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<PublishResult> PublishGlobalAsync(CancellationToken cancellationToken);

    Task<RebuildCacheResult> RebuildCacheAsync(CancellationToken cancellationToken);
}
