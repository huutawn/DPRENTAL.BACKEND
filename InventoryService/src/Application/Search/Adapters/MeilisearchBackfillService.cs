using System.Diagnostics;
using System.Text.Json;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Meilisearch.QueryParameters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Search.Adapters;

/// <summary>Committed Outbox is the replay journal; a short mutation barrier closes the snapshot/swap gap.</summary>
public sealed partial class MeilisearchBackfillService(IServiceScopeFactory scopeFactory,
    MeilisearchClient client, IOptions<MeilisearchOptions> options, TimeProvider? timeProvider = null,
    ILogger<MeilisearchBackfillService>? logger = null) : IInventorySearchBackfillService
{
    private readonly MeilisearchOptions _options = options.Value;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private const int BatchSize = 1000;

    public async Task<BackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        await using var runLock = await store.TryAcquireCoordinationAsync(SearchCoordination.RunKey, cancellationToken);
        if (runLock is null) throw new InvalidOperationException("Another backfill owns the run lock.");
        var old = await store.GetActiveBackfillRunAsync(cancellationToken);
        if (old?.Status == SearchBackfillRun.StatusSwapping)
        {
            await using var writer = await AcquireAsync(store, SearchCoordination.WriterKey, cancellationToken);
            await RecoverSwapAsync(old, store, cancellationToken);
            return new BackfillResult(0, 0, 0, elapsed.Elapsed);
        }
        if (old is not null)
        {
            // An interrupted staging upload may have unknown requests in flight. Never reuse it for a swap.
            old.Transition(SearchBackfillRun.StatusFailed, _clock.GetUtcNow(), "Interrupted before swap; superseded by a fresh staging generation.");
            await store.SaveBackfillRunAsync(old, cancellationToken);
        }
        var generation = await store.GetGenerationAsync(cancellationToken) ?? SearchGeneration.CreateInitial(
            _options.PublicListingsIndex, _options.OwnerListingsIndex, _options.OwnerAssetsIndex, _clock.GetUtcNow());
        if (generation.ErrorCode == "SEARCH_SUBMISSION_UNKNOWN")
            throw new InvalidOperationException("Reconcile unknown live submissions before rebuilding.");
        var id = Guid.NewGuid();
        var run = SearchBackfillRun.Create(id, $"{_options.PublicListingsIndex}_staging_{id:N}",
            $"{_options.OwnerListingsIndex}_staging_{id:N}", $"{_options.OwnerAssetsIndex}_staging_{id:N}", null, _clock.GetUtcNow());
        await using (var writer = await AcquireAsync(store, SearchCoordination.WriterKey, cancellationToken))
        await using (var barrier = await AcquireAsync(store, SearchCoordination.MutationBarrierKey, cancellationToken))
        {
            generation = await store.GetGenerationAsync(cancellationToken) ?? generation;
            if (generation.ErrorCode == "SEARCH_SUBMISSION_UNKNOWN" || await store.HasUncertainSubmissionsAsync(cancellationToken))
                throw new InvalidOperationException("Unresolved live submission blocks a new generation.");
            if (generation.ErrorCode == "SEARCH_REBUILD_REQUIRED") generation.MarkUnavailable(null, _clock.GetUtcNow());
            generation.SetActiveRun(id, _clock.GetUtcNow());
            await store.SaveBackfillStateAsync(run, generation, cancellationToken);
        }
        try
        {
            await using (var writer = await AcquireAsync(store, SearchCoordination.WriterKey, cancellationToken))
            {
                if (await store.HasUncertainSubmissionsAsync(cancellationToken))
                {
                    generation.MarkUnavailable("SEARCH_SUBMISSION_UNKNOWN", _clock.GetUtcNow());
                    await store.SaveGenerationAsync(generation, cancellationToken);
                    throw new InvalidOperationException("Unresolved live submissions block rebuilding.");
                }
                foreach (var uid in await store.ReadPendingTaskIdsAsync(cancellationToken))
                    await client.WaitForTaskAsync(uid, timeoutMs: _options.TaskDeadlineSeconds * 1000.0, cancellationToken: cancellationToken);
                var provisioner = scope.ServiceProvider.GetRequiredService<IInventoryIndexProvisioner>();
                await provisioner.EnsureIndexesAndSettingsAsync(cancellationToken);
                await provisioner.ConfigureIndexesAsync(run.StagingPublicIndex, run.StagingOwnerListingsIndex,
                    run.StagingOwnerAssetsIndex, cancellationToken);
            }
            var counts = await LoadSnapshotAsync(run, store, cancellationToken);
            run.Transition(SearchBackfillRun.StatusCatchingUp, _clock.GetUtcNow());
            await store.SaveBackfillRunAsync(run, cancellationToken);
            while (true)
            {
                await ReplayJournalAsync(run, store, cancellationToken);
                await using var writer = await AcquireAsync(store, SearchCoordination.WriterKey, cancellationToken);
                using var cutover = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cutover.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await using var barrier = await AcquireAsync(store, SearchCoordination.MutationBarrierKey, cutover.Token);
                    await ReplayJournalAsync(run, store, cutover.Token, writerHeld: true);
                    generation = await store.GetGenerationAsync(cutover.Token) ?? generation;
                    if (generation.ErrorCode == "SEARCH_SUBMISSION_UNKNOWN")
                        throw new InvalidOperationException("Unresolved live submission blocks cutover.");
                    generation.MarkUnavailable(null, _clock.GetUtcNow());
                    run.Transition(SearchBackfillRun.StatusSwapping, _clock.GetUtcNow());
                    await store.SaveBackfillStateAsync(run, generation, cutover.Token);
                    // Release domain writes after persisting the UID. Writer remains owned until the swap is terminal.
                    var task = await client.SwapIndexesAsync(new List<IndexSwap>
                    {
                        new(_options.PublicListingsIndex, run.StagingPublicIndex),
                        new(_options.OwnerListingsIndex, run.StagingOwnerListingsIndex),
                        new(_options.OwnerAssetsIndex, run.StagingOwnerAssetsIndex)
                    }, cutover.Token);
                    run.RecordSwap(task.TaskUid, _clock.GetUtcNow());
                    using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await store.SaveBackfillRunAsync(run, persist.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && run.Status != SearchBackfillRun.StatusSwapping)
                { continue; }
                await RecoverSwapAsync(run, store, cancellationToken);
                return new BackfillResult(counts.Public, counts.Listings, counts.Assets, elapsed.Elapsed);
            }
        }
        catch (Exception ex)
        {
            using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (run.Status != SearchBackfillRun.StatusSwapping)
            {
                run.Transition(SearchBackfillRun.StatusFailed, _clock.GetUtcNow(), "Backfill failed before cutover.");
                await store.SaveBackfillRunAsync(run, persist.Token);
                var current = await store.GetGenerationAsync(persist.Token);
                if (current is not null && current.ActiveRunId == run.RunId)
                { current.SetActiveRun(null, _clock.GetUtcNow()); await store.SaveGenerationAsync(current, persist.Token); }
            }
            logger?.LogWarning(ex, "Backfill run {RunId} failed; durable state retained.", run.RunId);
            throw;
        }
    }

    private static async Task<IAsyncDisposable> AcquireAsync(ISearchProjectionStore store, long key, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var acquired = await store.TryAcquireCoordinationAsync(key, ct);
            if (acquired is not null) return acquired;
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    private async Task RecoverSwapAsync(SearchBackfillRun run,
        ISearchProjectionStore store, CancellationToken ct)
    {
        if (run.SwapTaskUid is null)
        {
            // Staging names contain a unique run UUID. Task history can safely identify its one swap.
            int? from = null;
            do
            {
                var tasks = await client.GetTasksAsync(new TasksQuery { Limit = 1000, From = from }, ct);
                var matching = tasks.Results.Where(t => t.Type == TaskInfoType.IndexSwap &&
                    JsonSerializer.Serialize(t.Details).Contains(run.StagingPublicIndex, StringComparison.Ordinal)).ToList();
                if (matching.Count > 1) throw new InvalidOperationException("Ambiguous swap task history; operator reconciliation required.");
                if (matching.Count == 1) { run.RecordSwap(matching[0].Uid, _clock.GetUtcNow()); break; }
                from = tasks.Results.Any() ? tasks.Results.Min(t => t.Uid) - 1 : null;
            } while (from.HasValue);
            if (run.SwapTaskUid is null) throw new InvalidOperationException("Swap submission unresolved; do not resubmit blindly.");
            await store.SaveBackfillRunAsync(run, ct);
        }
        var task = await client.WaitForTaskAsync(run.SwapTaskUid.Value,
            timeoutMs: _options.TaskDeadlineSeconds * 1000.0, cancellationToken: ct);
        var generation = await store.GetGenerationAsync(ct) ?? throw new InvalidOperationException("Missing search generation.");
        {
            if (task.Status == TaskInfoStatus.Succeeded)
            {
                if (generation.ErrorCode == "SEARCH_REBUILD_REQUIRED") generation.MarkUnavailable(null, _clock.GetUtcNow());
                else generation.MarkReady(_clock.GetUtcNow());
                run.Transition(SearchBackfillRun.StatusCompleted, _clock.GetUtcNow());
            }
            else
            { generation.MarkUnavailable(null, _clock.GetUtcNow()); run.Transition(SearchBackfillRun.StatusFailed, _clock.GetUtcNow(), "Swap task failed."); }
            generation.SetActiveRun(null, _clock.GetUtcNow());
            await store.SaveBackfillStateAsync(run, generation, ct);
        }
        if (task.Status != TaskInfoStatus.Succeeded) throw new InvalidOperationException("Swap failed; a fresh build is required.");
        foreach (var uid in new[] { run.StagingPublicIndex, run.StagingOwnerListingsIndex, run.StagingOwnerAssetsIndex })
        {
            try { var deletion = await client.DeleteIndexAsync(uid, ct); await WaitAsync(deletion.TaskUid, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger?.LogWarning(ex, "Old search index cleanup failed for {IndexUid}.", uid); }
        }
    }

    private async Task WaitAsync(int uid, CancellationToken ct)
    {
        var task = await client.WaitForTaskAsync(uid, timeoutMs: _options.TaskDeadlineSeconds * 1000.0, cancellationToken: ct);
        if (task.Status != TaskInfoStatus.Succeeded) throw new InvalidOperationException($"Search task {uid} did not succeed.");
    }
}
