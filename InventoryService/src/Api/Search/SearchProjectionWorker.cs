using System.Data;
using System.Security.Cryptography;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Api.Search;

/// <summary>
/// Background worker tuần tự hóa index writes qua PostgreSQL session advisory lock,
/// claim batch với lease 30s / renew 10s, resume task tracking khi restart, và keyset fanout.
/// </summary>
public sealed class SearchProjectionWorker : BackgroundService
{
    public const long SessionAdvisoryLockKey = 0x494E565F53524348; // "INV_SRCH"
    public const int FanoutBatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MeilisearchClient _meiliClient;
    private readonly MeilisearchOptions _meiliOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SearchProjectionWorker>? _logger;

    public int BatchSize { get; set; } = 20;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public SearchProjectionWorker(
        IServiceScopeFactory scopeFactory,
        MeilisearchClient meiliClient,
        IOptions<MeilisearchOptions> meiliOptions,
        TimeProvider? timeProvider = null,
        ILogger<SearchProjectionWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _meiliClient = meiliClient ?? throw new ArgumentNullException(nameof(meiliClient));
        _meiliOptions = meiliOptions?.Value ?? throw new ArgumentNullException(nameof(meiliOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("SearchProjectionWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessBatchAsync(stoppingToken);
                if (processedCount == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
                else if (processedCount < BatchSize)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi ngoài dự kiến trong SearchProjectionWorker loop.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger?.LogInformation("SearchProjectionWorker đã dừng.");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid();

        IReadOnlyList<SearchProjectionJob> jobs;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
            jobs = await store.ClaimPendingBatchAsync(BatchSize, leaseToken, LeaseDuration, now, stoppingToken);
        }

        if (jobs.Count == 0)
        {
            return 0;
        }

        // Tuần tự hóa các lần ghi Meilisearch index qua PostgreSQL session advisory lock
        await using var lockScope = _scopeFactory.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        bool acquiredLock = false;

        try
        {
            if (lockDb.Database.IsNpgsql())
            {
                var conn = lockDb.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open) await conn.OpenAsync(stoppingToken);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT pg_try_advisory_lock({SessionAdvisoryLockKey});";
                var lockResult = await cmd.ExecuteScalarAsync(stoppingToken);
                acquiredLock = lockResult is bool b && b;

                if (!acquiredLock)
                {
                    _logger?.LogDebug("Replica khác đang giữ session advisory lock ghi index. Nhường lượt.");
                    return 0;
                }
            }

            foreach (var job in jobs)
            {
                await ProcessSingleJobWithLeaseAsync(job, leaseToken, stoppingToken);
            }
        }
        finally
        {
            if (acquiredLock && lockDb.Database.IsNpgsql())
            {
                try
                {
                    var conn = lockDb.Database.GetDbConnection();
                    if (conn.State == ConnectionState.Open)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SELECT pg_advisory_unlock({SessionAdvisoryLockKey});";
                        await cmd.ExecuteScalarAsync(CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Lỗi nhả session advisory lock.");
                }
            }
        }

        return jobs.Count;
    }

    private async Task ProcessSingleJobWithLeaseAsync(
        SearchProjectionJob job,
        Guid leaseToken,
        CancellationToken stoppingToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = jobCts.Token;

        using var renewTimer = new PeriodicTimer(RenewInterval);
        var renewTask = Task.Run(async () =>
        {
            try
            {
                while (await renewTimer.WaitForNextTickAsync(ct))
                {
                    await using var renewScope = _scopeFactory.CreateAsyncScope();
                    var renewStore = renewScope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
                    bool renewed = await renewStore.RenewLeaseAsync(job.Id, leaseToken, LeaseDuration, _timeProvider.GetUtcNow(), ct);
                    if (!renewed)
                    {
                        _logger?.LogWarning("Mất lease cho job {JobId}. Hủy tác vụ đang chạy.", job.Id);
                        jobCts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        try
        {
            await ProcessSingleJobCoreAsync(job, leaseToken, ct);
        }
        finally
        {
            jobCts.Cancel();
            try { await renewTask; } catch { }
        }
    }

    private async Task ProcessSingleJobCoreAsync(
        SearchProjectionJob job,
        Guid leaseToken,
        CancellationToken ct)
    {
        long targetRevision = job.ProcessingRevision ?? job.DesiredRevision;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        try
        {
            // 1. Kiểm tra nếu job đã có task UID đang chờ từ lần chạy trước (crash/timeout recovery)
            if (job.PendingTaskUids != null && job.PendingTaskUids.Length > 0)
            {
                bool allFinished = await TryResumePendingTasksAsync(job.PendingTaskUids, ct);
                if (allFinished)
                {
                    await FinishJobAsync(job, store, db, leaseToken, targetRevision, ct);
                    return;
                }
            }

            // 2. Thực hiện submission mới
            if (string.Equals(job.EntityType, "EquipmentListing", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessListingJobAsync(job, store, db, leaseToken, ct);
            }
            else if (string.Equals(job.EntityType, "InventoryAsset", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessAssetJobAsync(job, store, db, leaseToken, ct);
            }

            await FinishJobAsync(job, store, db, leaseToken, targetRevision, ct);
            _logger?.LogDebug("Đã hoàn tất đồng bộ Meilisearch cho {EntityType} {EntityId}.", job.EntityType, job.EntityId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = _timeProvider.GetUtcNow();
            _logger?.LogWarning(ex, "Lỗi đồng bộ Meilisearch cho {EntityType} {EntityId} (Lần thử {Attempt}/10). Lên lịch retry.",
                job.EntityType, job.EntityId, job.RetryCount + 1);

            int backoffSec = CalculateBackoffWithJitter(job.RetryCount + 1);
            var nextAttemptAt = now.AddSeconds(backoffSec);
            await store.MarkFailedAsync(job.Id, leaseToken, targetRevision, ex.Message, nextAttemptAt, now, CancellationToken.None);
        }
    }

    private async Task<bool> TryResumePendingTasksAsync(int[] taskUids, CancellationToken ct)
    {
        foreach (var uid in taskUids)
        {
            var task = await _meiliClient.GetTaskAsync(uid, ct);
            if (task.Status == TaskInfoStatus.Failed)
            {
                throw new InvalidOperationException($"Meilisearch task {uid} đã thất bại: {task.Error?.GetValueOrDefault("message")}");
            }
            if (task.Status != TaskInfoStatus.Succeeded)
            {
                // Task vẫn đang chạy -> tiếp tục chờ
                await WaitForTaskStrictAsync(uid, ct);
            }
        }
        return true;
    }

    private async Task ProcessListingJobAsync(
        SearchProjectionJob job,
        ISearchProjectionStore store,
        InventoryServiceDbContext db,
        Guid leaseToken,
        CancellationToken ct)
    {
        var publicIndex = _meiliClient.Index(_meiliOptions.PublicListingsIndex);
        var ownerIndex = _meiliClient.Index(_meiliOptions.OwnerListingsIndex);

        var listing = await db.EquipmentListings
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == job.EntityId, ct);

        if (listing is null || string.Equals(job.Action, SearchProjectionJob.ActionDelete, StringComparison.OrdinalIgnoreCase))
        {
            var t1 = await publicIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), ct);
            var t2 = await ownerIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), ct);
            await store.SavePendingTasksAsync(job.Id, leaseToken, [t1.TaskUid, t2.TaskUid], _timeProvider.GetUtcNow(), ct);
            await WaitForTaskStrictAsync(t1.TaskUid, ct);
            await WaitForTaskStrictAsync(t2.TaskUid, ct);
            return;
        }

        var submittedTasks = new List<int>();

        // 1. Owner Index
        var ownerDoc = SearchProjectionMapper.ToOwnerListingDocument(listing);
        var ownerTask = await ownerIndex.AddDocumentsAsync([ownerDoc], primaryKey: "id", cancellationToken: ct);
        submittedTasks.Add(ownerTask.TaskUid);

        // 2. Public Index
        if (listing.Status == EquipmentListingStatus.Active)
        {
            var publicDoc = SearchProjectionMapper.ToPublicListingDocument(listing);
            var pubTask = await publicIndex.AddDocumentsAsync([publicDoc], primaryKey: "id", cancellationToken: ct);
            submittedTasks.Add(pubTask.TaskUid);
        }
        else
        {
            var delTask = await publicIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), ct);
            submittedTasks.Add(delTask.TaskUid);
        }

        // Lưu task UIDs ngay sau submission
        await store.SavePendingTasksAsync(job.Id, leaseToken, submittedTasks, _timeProvider.GetUtcNow(), ct);

        foreach (var taskUid in submittedTasks)
        {
            await WaitForTaskStrictAsync(taskUid, ct);
        }
    }

    private async Task ProcessAssetJobAsync(
        SearchProjectionJob job,
        ISearchProjectionStore store,
        InventoryServiceDbContext db,
        Guid leaseToken,
        CancellationToken ct)
    {
        var assetIndex = _meiliClient.Index(_meiliOptions.OwnerAssetsIndex);

        var asset = await db.Assets
            .Include(a => a.Listing)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == job.EntityId, ct);

        if (asset is null || string.Equals(job.Action, SearchProjectionJob.ActionDelete, StringComparison.OrdinalIgnoreCase))
        {
            var delTask = await assetIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), ct);
            await store.SavePendingTasksAsync(job.Id, leaseToken, [delTask.TaskUid], _timeProvider.GetUtcNow(), ct);
            await WaitForTaskStrictAsync(delTask.TaskUid, ct);
            return;
        }

        var assetDoc = SearchProjectionMapper.ToOwnerAssetDocument(asset);
        var task = await assetIndex.AddDocumentsAsync([assetDoc], primaryKey: "id", cancellationToken: ct);
        await store.SavePendingTasksAsync(job.Id, leaseToken, [task.TaskUid], _timeProvider.GetUtcNow(), ct);
        await WaitForTaskStrictAsync(task.TaskUid, ct);
    }

    private async Task FinishJobAsync(
        SearchProjectionJob job,
        ISearchProjectionStore store,
        InventoryServiceDbContext db,
        Guid leaseToken,
        long targetRevision,
        CancellationToken ct)
    {
        // Xử lý keyset fan-out asset con nếu có
        if (string.Equals(job.EntityType, "EquipmentListing", StringComparison.OrdinalIgnoreCase) &&
            job.NeedsAssetFanout && !job.FanoutCompleted)
        {
            await ProcessAssetFanoutKeysetAsync(job, store, db, leaseToken, targetRevision, ct);
        }

        var now = _timeProvider.GetUtcNow();
        await store.MarkCompletedAsync(job.Id, leaseToken, targetRevision, now, ct);
    }

    private async Task ProcessAssetFanoutKeysetAsync(
        SearchProjectionJob job,
        ISearchProjectionStore store,
        InventoryServiceDbContext db,
        Guid leaseToken,
        long targetRevision,
        CancellationToken ct)
    {
        Guid? currentCursor = job.FanoutCursor;

        while (!ct.IsCancellationRequested)
        {
            var query = db.Assets
                .AsNoTracking()
                .Where(a => a.EquipmentListingId == job.EntityId);

            if (currentCursor.HasValue)
            {
                query = query.Where(a => a.Id > currentCursor.Value);
            }

            var batch = await query
                .OrderBy(a => a.Id)
                .Take(FanoutBatchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                await store.SaveFanoutProgressAsync(job.Id, leaseToken, currentCursor, completed: true, _timeProvider.GetUtcNow(), ct);
                break;
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var assetId in batch)
            {
                await store.UpsertJobAsync("InventoryAsset", assetId, "Upsert", targetRevision, false, now, ct);
            }

            currentCursor = batch[^1];
            bool isComplete = batch.Count < FanoutBatchSize;
            await store.SaveFanoutProgressAsync(job.Id, leaseToken, currentCursor, completed: isComplete, now, ct);

            if (isComplete) break;
        }
    }

    private async Task WaitForTaskStrictAsync(int taskUid, CancellationToken ct)
    {
        double timeoutMs = TimeSpan.FromSeconds(_meiliOptions.TaskDeadlineSeconds).TotalMilliseconds;
        var task = await _meiliClient.WaitForTaskAsync(taskUid, timeoutMs: timeoutMs, cancellationToken: ct);
        if (task.Status != TaskInfoStatus.Succeeded)
        {
            var errorMsg = task.Error != null ? string.Join(", ", task.Error.Select(kv => $"{kv.Key}: {kv.Value}")) : "Task failed";
            throw new InvalidOperationException($"Meilisearch task {taskUid} kết thúc với trạng thái {task.Status}: {errorMsg}");
        }
    }

    private static int CalculateBackoffWithJitter(int retryCount)
    {
        // Exponential backoff trần 60s có jitter
        double baseSec = Math.Min(60.0, Math.Pow(2, Math.Min(retryCount, 6)));
        double jitter = (RandomNumberGenerator.GetInt32(0, 40) - 20) / 100.0 * baseSec; // +/- 20%
        return Math.Max(1, (int)Math.Round(baseSec + jitter));
    }
}
