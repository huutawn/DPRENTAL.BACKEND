using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Worker nền chuyên trách xử lý các công việc xóa cache (Cache Invalidation Jobs) độc lập với Kafka.
/// Claim các job bằng lease và SKIP LOCKED, xử lý tối đa 4 job đồng thời với DI scope riêng,
/// giới hạn 32 Valkey invalidations đồng thời, hỗ trợ warehouse keyset fanout với checkpoint cursor,
/// và dọn dẹp các job Completed quá 7 ngày theo lô 500 record.
/// </summary>
public sealed class CacheInvalidationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInventoryReadCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CacheInvalidationWorker>? _logger;

    private readonly SemaphoreSlim _valkeyInvalidationSemaphore = new(32, 32);
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;

    public int BatchSize { get; set; } = 100;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public int MaxDegreeOfParallelism { get; set; } = 4;

    public CacheInvalidationWorker(
        IServiceScopeFactory scopeFactory,
        IInventoryReadCache cache,
        TimeProvider? timeProvider = null,
        ILogger<CacheInvalidationWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("CacheInvalidationWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessBatchAsync(stoppingToken);

                // Dọn dẹp định kỳ completed jobs quá 7 ngày
                await TryCleanupExpiredCompletedJobsAsync(stoppingToken);

                if (processedCount == 0)
                {
                    await Task.Delay(IdleDelay, _timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi ngoài dự kiến trong vòng lặp CacheInvalidationWorker.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger?.LogInformation("CacheInvalidationWorker đã dừng.");
    }

    /// <summary>
    /// Xử lý một đợt các job pending hoặc lease đã hết hạn.
    /// Trả về số lượng công việc đã claim.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICacheInvalidationStore>();

        var nowUtc = _timeProvider.GetUtcNow();
        var jobs = await store.ClaimEligibleJobsAsync(BatchSize, LeaseDuration, nowUtc, cancellationToken);
        if (jobs.Count == 0)
        {
            return 0;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(jobs, parallelOptions, async (job, ct) =>
        {
            await ProcessSingleJobAsync(job, ct);
        });

        return jobs.Count;
    }

    private async Task ProcessSingleJobAsync(CacheInvalidationJob job, CancellationToken cancellationToken)
    {
        if (!job.LockLeaseToken.HasValue)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICacheInvalidationStore>();
        var listingRepo = scope.ServiceProvider.GetRequiredService<IEquipmentListingRepository>();

        var nowUtc = _timeProvider.GetUtcNow();
        if (job.LockLeaseExpiresAt.HasValue && job.LockLeaseExpiresAt.Value <= nowUtc.AddSeconds(2))
        {
            _logger?.LogWarning("Lease của Invalidation job {JobId} sắp hoặc đã hết hạn ({Expiry}). Bỏ qua lượt này.", job.Id, job.LockLeaseExpiresAt);
            return;
        }

        try
        {
            if (job.IsFanout)
            {
                await ProcessFanoutJobAsync(job, store, listingRepo, cancellationToken);
            }
            else
            {
                await ProcessDirectScopesJobAsync(job, store, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lỗi invalidate cache cho job {JobId}. Lên lịch retry.", job.Id);

            int nextRetry = job.RetryCount + 1;
            bool isTerminal = nextRetry >= 10;
            var backoffSeconds = Math.Min(60, Math.Pow(2, nextRetry));
            var nextAttempt = _timeProvider.GetUtcNow().AddSeconds(backoffSeconds);

            await store.MarkAsFailedAsync(
                job.Id,
                job.LockLeaseToken.Value,
                nextRetry,
                nextAttempt,
                ex.Message,
                isTerminal,
                cancellationToken);
        }
    }

    private async Task ProcessDirectScopesJobAsync(
        CacheInvalidationJob job,
        ICacheInvalidationStore store,
        CancellationToken cancellationToken)
    {
        await _valkeyInvalidationSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _cache.InvalidateScopesAsync(job.Scopes, cancellationToken);
        }
        finally
        {
            _valkeyInvalidationSemaphore.Release();
        }

        var completedAt = _timeProvider.GetUtcNow();
        await store.MarkAsCompletedAsync(job.Id, job.LockLeaseToken!.Value, completedAt, cancellationToken);
    }

    private async Task ProcessFanoutJobAsync(
        CacheInvalidationJob job,
        ICacheInvalidationStore store,
        IEquipmentListingRepository listingRepo,
        CancellationToken cancellationToken)
    {
        // 1. Invalidate initial scopes nếu còn (ví dụ: owner, public-discovery ở lần đầu)
        if (job.Scopes.Length > 0)
        {
            await _valkeyInvalidationSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _cache.InvalidateScopesAsync(job.Scopes, cancellationToken);
            }
            finally
            {
                _valkeyInvalidationSemaphore.Release();
            }
        }

        // 2. Keyset pagination đọc tối đa 100 listings/page
        const int pageSize = 100;
        var listingIds = await listingRepo.GetListingIdsByOwnerKeysetAsync(
            job.FanoutOwnerUserId!.Value,
            job.FanoutAfterListingId,
            pageSize,
            cancellationToken);

        if (listingIds.Count > 0)
        {
            var listingScopes = listingIds.Select(id => $"listing:{id:D}").ToList();

            await _valkeyInvalidationSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _cache.InvalidateScopesAsync(listingScopes, cancellationToken);
            }
            finally
            {
                _valkeyInvalidationSemaphore.Release();
            }
        }

        // 3. Nếu còn trang tiếp theo: checkpoint cursor và nhả về Pending
        if (listingIds.Count == pageSize)
        {
            var lastId = listingIds[^1];
            await store.CheckpointFanoutAsync(job.Id, job.LockLeaseToken!.Value, lastId, _timeProvider.GetUtcNow(), cancellationToken);
        }
        else
        {
            // Đã duyệt hết tất cả listings của owner -> hoàn thành job
            var completedAt = _timeProvider.GetUtcNow();
            await store.MarkAsCompletedAsync(job.Id, job.LockLeaseToken!.Value, completedAt, cancellationToken);
        }
    }

    private async Task TryCleanupExpiredCompletedJobsAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastCleanupTime < TimeSpan.FromHours(1))
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICacheInvalidationStore>();
            var olderThan = now.AddDays(-7);
            await store.CleanupCompletedJobsAsync(olderThan, 500, cancellationToken);
            _lastCleanupTime = now;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lỗi trong quá trình dọn dẹp các job Completed cũ.");
        }
    }

    public override void Dispose()
    {
        _valkeyInvalidationSemaphore.Dispose();
        base.Dispose();
    }
}
