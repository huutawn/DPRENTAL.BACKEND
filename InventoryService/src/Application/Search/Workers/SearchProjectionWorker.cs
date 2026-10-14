using System.Security.Cryptography;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Search.Workers;

public sealed class SearchProjectionWorker(IServiceScopeFactory scopeFactory, MeilisearchClient client,
    IOptions<MeilisearchOptions> options, TimeProvider? timeProvider = null,
    ILogger<SearchProjectionWorker>? logger = null) : BackgroundService
{
    public const long SessionAdvisoryLockKey = SearchCoordination.WriterKey;
    public const int FanoutBatchSize = 500;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly MeilisearchOptions _options = options.Value;
    public int BatchSize { get; set; } = 20;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessBatchAsync(stoppingToken) == 0) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Search projection worker failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        await using var writer = await store.TryAcquireCoordinationAsync(SearchCoordination.WriterKey, ct);
        if (writer is null) return 0;
        var generation = await store.GetGenerationAsync(ct);
        if (generation?.IsReady != true) return 0;
        if (await store.HasUncertainSubmissionsAsync(ct))
        { await QuarantineAsync(store, ct); return 0; }
        int processed = 0;
        while (processed < BatchSize)
        {
            var lease = Guid.NewGuid();
            var jobs = await store.ClaimPendingBatchAsync(1, lease, LeaseDuration, _clock.GetUtcNow(), ct);
            if (jobs.Count == 0) break;
            await ProcessWithLeaseAsync(jobs[0], lease, ct);
            processed++;
            if ((await store.GetGenerationAsync(ct))?.IsReady != true) break;
        }
        return processed;
    }

    private async Task ProcessWithLeaseAsync(SearchProjectionJob job, Guid lease, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewal = new PeriodicTimer(RenewInterval, _clock);
        var renewTask = RenewAsync(job.Id, lease, renewal, linked);
        try { await ProcessJobAsync(job, lease, linked.Token); }
        finally
        {
            await linked.CancelAsync();
            await renewTask;
        }
    }

    private async Task RenewAsync(Guid jobId, Guid lease, PeriodicTimer timer, CancellationTokenSource linked)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
                if (!await store.RenewLeaseAsync(jobId, lease, LeaseDuration, _clock.GetUtcNow(), linked.Token))
                {
                    await linked.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Search lease renewal failed for {JobId}.", jobId);
            await linked.CancelAsync();
        }
    }

    private async Task ProcessJobAsync(SearchProjectionJob job, Guid lease, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        var revision = job.ProcessingRevision ?? throw new InvalidOperationException("Missing processing revision.");
        var taskIds = job.PendingTaskUids.ToList();
        bool uncertain = job.SubmissionUncertain;
        try
        {
            if (uncertain) throw new UnknownSearchSubmissionException();
            if (taskIds.Count > 0)
            {
                bool succeeded = await SettleTasksAsync(taskIds, ct);
                if (!succeeded || !job.SubmissionComplete || job.SubmissionRevision is null)
                {
                    Require(await store.ResetSubmissionAsync(job.Id, lease, _clock.GetUtcNow(), ct));
                    throw new InvalidOperationException("Previous submission ended; retry the complete current projection.");
                }
            }
            else
            {
                var actions = await BuildSubmissionAsync(job, store, ct);
                foreach (var submit in actions)
                {
                    uncertain = true;
                    Require(await store.SaveSubmissionAsync(job.Id, lease, revision, taskIds, true, false, _clock.GetUtcNow(), ct));
                    TaskInfo task;
                    try { task = await submit(ct); }
                    catch (MeilisearchApiError ex) when (ex.Code is "invalid_api_key" or "missing_authorization_header" or
                        "missing_document_id" or "invalid_document_id" or "index_not_found")
                    {
                        uncertain = false; // A definitive API rejection did not enqueue a task.
                        Require(await store.SaveSubmissionAsync(job.Id, lease, revision, taskIds, false, false, _clock.GetUtcNow(), ct));
                        throw;
                    }
                    taskIds.Add(task.TaskUid);
                    Require(await store.SaveSubmissionAsync(job.Id, lease, revision, taskIds, false,
                        taskIds.Count == actions.Count, _clock.GetUtcNow(), ct));
                    uncertain = false;
                }
                if (!await SettleTasksAsync(taskIds, ct))
                {
                    Require(await store.ResetSubmissionAsync(job.Id, lease, _clock.GetUtcNow(), ct));
                    throw new InvalidOperationException("Meilisearch rejected the projection; retry submission.");
                }
            }
            if (job.FanoutRequestedRevision > job.FanoutAppliedRevision)
                await FanoutAsync(job, lease, revision, store, ct);
            Require(await store.MarkCompletedAsync(job.Id, lease, revision, _clock.GetUtcNow(), ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (uncertain) await QuarantineAsync(store, ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            if (uncertain || ex is UnknownSearchSubmissionException) await QuarantineAsync(store, ct);
            var now = _clock.GetUtcNow();
            int delay = Math.Clamp((int)(Math.Min(60, Math.Pow(2, job.RetryCount + 1)) *
                (0.8 + RandomNumberGenerator.GetInt32(0, 41) / 100.0)), 1, 60);
            await store.MarkFailedAsync(job.Id, lease, revision, "Search projection failed; see operational diagnostics.",
                now.AddSeconds(delay), now, ct);
            logger?.LogWarning(ex, "Search projection {JobId} revision {Revision} failed.", job.Id, revision);
        }
    }

    private async Task<List<Func<CancellationToken, Task<TaskInfo>>>> BuildSubmissionAsync(
        SearchProjectionJob job, ISearchProjectionStore store, CancellationToken ct)
    {
        var actions = new List<Func<CancellationToken, Task<TaskInfo>>>();
        if (job.EntityType == "EquipmentListing")
        {
            var listing = (await store.ReadListingsAsync(null, 1, [job.EntityId], ct)).FirstOrDefault();
            var owner = client.Index(_options.OwnerListingsIndex);
            var publicIndex = client.Index(_options.PublicListingsIndex);
            if (listing is null)
            {
                actions.Add(token => owner.DeleteOneDocumentAsync(job.EntityId.ToString("D"), token));
                actions.Add(token => publicIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), token));
            }
            else
            {
                actions.Add(token => owner.AddDocumentsAsync([SearchProjectionMapper.ToOwnerListingDocument(listing)], "id", token));
                actions.Add(listing.Status == EquipmentListingStatus.Active
                    ? token => publicIndex.AddDocumentsAsync([SearchProjectionMapper.ToPublicListingDocument(listing)], "id", token)
                    : token => publicIndex.DeleteOneDocumentAsync(job.EntityId.ToString("D"), token));
            }
        }
        else if (job.EntityType == "InventoryAsset")
        {
            var asset = (await store.ReadAssetsAsync(null, 1, [job.EntityId], cancellationToken: ct)).FirstOrDefault();
            var index = client.Index(_options.OwnerAssetsIndex);
            actions.Add(asset is null
                ? token => index.DeleteOneDocumentAsync(job.EntityId.ToString("D"), token)
                : token => index.AddDocumentsAsync([SearchProjectionMapper.ToOwnerAssetDocument(asset)], "id", token));
        }
        else throw new InvalidOperationException("Unsupported projection entity type.");
        return actions;
    }

    private async Task<bool> SettleTasksAsync(IEnumerable<int> taskIds, CancellationToken ct)
    {
        bool succeeded = true;
        foreach (var id in taskIds)
        {
            var task = await client.WaitForTaskAsync(id, timeoutMs: _options.TaskDeadlineSeconds * 1000.0, cancellationToken: ct);
            succeeded &= task.Status == TaskInfoStatus.Succeeded;
        }
        return succeeded;
    }

    private async Task FanoutAsync(SearchProjectionJob job, Guid lease, long revision,
        ISearchProjectionStore store, CancellationToken ct)
    {
        Guid? cursor = job.FanoutRevision == revision ? job.FanoutCursor : null;
        while (true)
        {
            var rows = await store.ReadAssetsAsync(cursor, FanoutBatchSize, listingIds: [job.EntityId], cancellationToken: ct);
            var ids = rows.Select(a => a.Id).ToList();
            foreach (var id in ids)
                await store.UpsertJobAsync("InventoryAsset", id, "Upsert", revision, false, _clock.GetUtcNow(), ct);
            if (ids.Count > 0) cursor = ids[^1];
            bool complete = ids.Count < FanoutBatchSize;
            Require(await store.SaveFanoutProgressAsync(job.Id, lease, revision, cursor, complete, _clock.GetUtcNow(), ct));
            if (complete) return;
        }
    }

    private async Task QuarantineAsync(ISearchProjectionStore store, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var generation = await store.GetGenerationAsync(deadline.Token);
        if (generation is null) return;
        generation.MarkUnavailable("SEARCH_SUBMISSION_UNKNOWN", _clock.GetUtcNow());
        await store.SaveGenerationAsync(generation, deadline.Token);
    }

    private static void Require(bool owned)
    { if (!owned) throw new OperationCanceledException("The search projection lease is no longer owned."); }
}

internal sealed class UnknownSearchSubmissionException : Exception
{
    public UnknownSearchSubmissionException() : base("Unresolved submission: reconcile task history before resuming index writes.") { }
}
