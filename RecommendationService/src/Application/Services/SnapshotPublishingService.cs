using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RecommendationService.Application.Services;

public sealed class SnapshotPublishingService : ISnapshotPublishingService
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan PublishDeadline = TimeSpan.FromSeconds(5);
    private const int UserPageSize = 100;
    private const int MaxUserInterests = 20;
    private const int MaxGlobalPopularListings = 300;

    private readonly IUserEquipmentTypeInterestRepository _userInterestRepository;
    private readonly IListingPopularityRepository _listingPopularityRepository;
    private readonly IValkeyProjectionRepository _valkeyRepository;
    private readonly ILogger<SnapshotPublishingService> _logger;

    public SnapshotPublishingService(
        IUserEquipmentTypeInterestRepository userInterestRepository,
        IListingPopularityRepository listingPopularityRepository,
        IValkeyProjectionRepository valkeyRepository,
        ILogger<SnapshotPublishingService> logger)
    {
        _userInterestRepository = userInterestRepository;
        _listingPopularityRepository = listingPopularityRepository;
        _valkeyRepository = valkeyRepository;
        _logger = logger;
    }

    public async Task<PublishResult> PublishUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var internalCts = new CancellationTokenSource(PublishDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, internalCts.Token);
        var token = linkedCts.Token;

        RankingLease? lease = null;
        try
        {
            var acquireResult = await _valkeyRepository.TryAcquireUserLeaseAsync(userId, token);
            if (acquireResult.Status == LeaseAcquireStatus.Busy)
            {
                return new PublishResult(PublishOutcome.Busy, acquireResult.Reason ?? "User ranking is busy.");
            }

            if (acquireResult.Status == LeaseAcquireStatus.Unavailable)
            {
                return new PublishResult(PublishOutcome.Unavailable, acquireResult.Reason ?? "Valkey is unavailable.");
            }

            if (acquireResult.Status == LeaseAcquireStatus.Failed || acquireResult.Lease == null)
            {
                return new PublishResult(PublishOutcome.Failed, acquireResult.Reason ?? "Failed to acquire user lease.");
            }

            lease = acquireResult.Lease;

            var items = await _userInterestRepository.GetTopInterestsAsync(userId, MaxUserInterests, token);
            var outcome = await _valkeyRepository.ReplaceUserRankingAsync(lease, items, SnapshotTtl, token);
            return new PublishResult(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (internalCts.IsCancellationRequested)
        {
            _logger.LogWarning("Publish user ranking for {UserId} exceeded 5-second deadline.", userId);
            return new PublishResult(PublishOutcome.Unavailable, "Publish user ranking timed out after 5 seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing user ranking for {UserId}.", userId);
            return new PublishResult(PublishOutcome.Failed, "An error occurred while publishing user ranking.");
        }
        finally
        {
            if (lease != null)
            {
                try
                {
                    await _valkeyRepository.ReleaseLeaseAsync(lease, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release user lease for {UserId} during cleanup.", userId);
                }
            }
        }
    }

    public async Task<PublishResult> PublishGlobalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var internalCts = new CancellationTokenSource(PublishDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, internalCts.Token);
        var token = linkedCts.Token;

        RankingLease? lease = null;
        try
        {
            var acquireResult = await _valkeyRepository.TryAcquireGlobalLeaseAsync(token);
            if (acquireResult.Status == LeaseAcquireStatus.Busy)
            {
                return new PublishResult(PublishOutcome.Busy, acquireResult.Reason ?? "Global ranking is busy.");
            }

            if (acquireResult.Status == LeaseAcquireStatus.Unavailable)
            {
                return new PublishResult(PublishOutcome.Unavailable, acquireResult.Reason ?? "Valkey is unavailable.");
            }

            if (acquireResult.Status == LeaseAcquireStatus.Failed || acquireResult.Lease == null)
            {
                return new PublishResult(PublishOutcome.Failed, acquireResult.Reason ?? "Failed to acquire global lease.");
            }

            lease = acquireResult.Lease;

            var items = await _listingPopularityRepository.GetTopPopularListingsAsync(MaxGlobalPopularListings, token);
            var outcome = await _valkeyRepository.ReplaceGlobalRankingAsync(lease, items, SnapshotTtl, token);
            return new PublishResult(outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (internalCts.IsCancellationRequested)
        {
            _logger.LogWarning("Publish global ranking exceeded 5-second deadline.");
            return new PublishResult(PublishOutcome.Unavailable, "Publish global ranking timed out after 5 seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing global ranking.");
            return new PublishResult(PublishOutcome.Failed, "An error occurred while publishing global ranking.");
        }
        finally
        {
            if (lease != null)
            {
                try
                {
                    await _valkeyRepository.ReleaseLeaseAsync(lease, token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release global lease during cleanup.");
                }
            }
        }
    }

    public async Task<RebuildCacheResult> RebuildCacheAsync(CancellationToken cancellationToken)
    {
        int totalEvaluated = 0;
        int publishedCount = 0;
        int busyCount = 0;
        int leaseLostCount = 0;
        int failedCount = 0;
        var errors = new List<RebuildErrorDetail>();

        // 1. Publish global popular listings first
        totalEvaluated++;
        var globalResult = await PublishGlobalAsync(cancellationToken);
        switch (globalResult.Outcome)
        {
            case PublishOutcome.Published:
                publishedCount++;
                break;
            case PublishOutcome.Busy:
                busyCount++;
                break;
            case PublishOutcome.LeaseLost:
                leaseLostCount++;
                break;
            case PublishOutcome.Unavailable:
                failedCount++;
                errors.Add(new RebuildErrorDetail("global", "VALKEY_UNAVAILABLE", globalResult.Reason ?? "Valkey is unavailable."));
                _logger.LogWarning("Valkey is unavailable during rebuild. Terminating rebuild cycle early.");
                return new RebuildCacheResult(totalEvaluated, publishedCount, busyCount, leaseLostCount, failedCount, errors);
            case PublishOutcome.Failed:
                failedCount++;
                errors.Add(new RebuildErrorDetail("global", "PUBLISH_FAILED", globalResult.Reason ?? "Failed to publish global ranking."));
                break;
        }

        // 2. Iterate all users with interest using cursor-based pagination
        Guid? afterUserId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Guid> userIds;
            try
            {
                userIds = await _userInterestRepository.GetDistinctUserIdsAsync(afterUserId, UserPageSize, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgreSQL query error during user enumeration cursor after {AfterUserId}. Aborting cycle.", afterUserId);
                errors.Add(new RebuildErrorDetail("user_enumeration", "POSTGRES_ERROR", "Failed to query distinct users from database."));
                break;
            }

            if (userIds.Count == 0)
            {
                break;
            }

            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalEvaluated++;

                var userResult = await PublishUserAsync(userId, cancellationToken);
                switch (userResult.Outcome)
                {
                    case PublishOutcome.Published:
                        publishedCount++;
                        break;
                    case PublishOutcome.Busy:
                        busyCount++;
                        break;
                    case PublishOutcome.LeaseLost:
                        leaseLostCount++;
                        break;
                    case PublishOutcome.Unavailable:
                        failedCount++;
                        errors.Add(new RebuildErrorDetail($"user:{userId:D}", "VALKEY_UNAVAILABLE", userResult.Reason ?? "Valkey is unavailable."));
                        _logger.LogWarning("Valkey is unavailable during user rebuild. Terminating rebuild cycle early.");
                        return new RebuildCacheResult(totalEvaluated, publishedCount, busyCount, leaseLostCount, failedCount, errors);
                    case PublishOutcome.Failed:
                        failedCount++;
                        errors.Add(new RebuildErrorDetail($"user:{userId:D}", "PUBLISH_FAILED", userResult.Reason ?? "Failed to publish user ranking."));
                        break;
                }

                afterUserId = userId;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Cache rebuild cycle summary: Evaluated={Total}, Published={Published}, Busy={Busy}, LeaseLost={LeaseLost}, Failed={Failed}, Errors={Errors}.",
            totalEvaluated, publishedCount, busyCount, leaseLostCount, failedCount, errors.Count);

        return new RebuildCacheResult(totalEvaluated, publishedCount, busyCount, leaseLostCount, failedCount, errors);
    }
}
