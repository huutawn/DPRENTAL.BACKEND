using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Common.Enums;
using Meilisearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests
{
    private static EquipmentListing NewListing(string name, bool active = true)
    {
        var now = DateTimeOffset.UtcNow;
        var listing = EquipmentListing.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            name, "Description", name, 1, "{}", 100000, null, null, "VND", now, 2024).Value;
        if (active) listing.UpdateStatus(EquipmentListingStatus.Active, true, now);
        return listing;
    }

    [Fact]
    public async Task Fanout_MoreThan500Assets_RepeatsForEveryNewMetadataRevision()
    {
        var listing = NewListing("Fanout camera");
        _db.EquipmentListings.Add(listing);
        for (int i = 0; i < 501; i++)
            _db.Assets.Add(InventoryAsset.Create(Guid.NewGuid(), listing.Id, $"SERIAL-{i}", DateTimeOffset.UtcNow, 2024).Value);
        await _db.SaveChangesAsync();
        await using var services = BuildSearchProvider();
        var worker = CreateWorker(services);
        worker.BatchSize = 1;
        await _store.UpsertJobAsync("EquipmentListing", listing.Id, "Upsert", 10, true, DateTimeOffset.UtcNow);
        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.Equal(501, await _db.SearchProjectionJobs.CountAsync(j => j.EntityType == "InventoryAsset" && j.DesiredRevision == 10));
        await _store.UpsertJobAsync("EquipmentListing", listing.Id, "Upsert", 11, true, DateTimeOffset.UtcNow);
        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.Equal(501, await _db.SearchProjectionJobs.CountAsync(j => j.EntityType == "InventoryAsset" && j.DesiredRevision == 11));
        var parent = await _db.SearchProjectionJobs.AsNoTracking().SingleAsync(j => j.EntityType == "EquipmentListing");
        Assert.Equal(11, parent.FanoutAppliedRevision);
        Assert.Equal(11, parent.AppliedRevision);
    }

    [Fact]
    public async Task OnlineBackfill_ReplaysAllUnpublishedEvents_InactivePrivateAndDeletedProjections()
    {
        var inactive = NewListing("Inactive old camera");
        var privateListing = NewListing("Private draft camera", active: false);
        var deleted = NewListing("Deleted camera");
        _db.EquipmentListings.AddRange(inactive, privateListing, deleted);
        var asset = InventoryAsset.Create(Guid.NewGuid(), inactive.Id, "DELETED-SERIAL", DateTimeOffset.UtcNow, 2024).Value;
        _db.Assets.Add(asset);
        await _db.SaveChangesAsync();
        using var handler = new AfterFirstStagingUploadHandler(async () =>
        {
            await using var db = CreateDb();
            var generation = await db.SearchGenerations.AsNoTracking().SingleAsync();
            Assert.NotNull(generation.ActiveRunId);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared({Dprental.InventoryService.Domain.Repositories.SearchCoordination.MutationBarrierKey})");
            var changed = await db.EquipmentListings.SingleAsync(l => l.Id == inactive.Id);
            changed.UpdateStatus(EquipmentListingStatus.Inactive, true, DateTimeOffset.UtcNow);
            db.EquipmentListings.Remove(await db.EquipmentListings.SingleAsync(l => l.Id == deleted.Id));
            db.Assets.Remove(await db.Assets.SingleAsync(a => a.Id == asset.Id));
            var now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 1002; i++)
                db.OutboxMessages.Add(SearchProjectionEvents.CreateListingProjectionOutboxMessage(inactive.Id, "Upsert", i + 1, true, now, generation.ActiveRunId));
            db.OutboxMessages.Add(SearchProjectionEvents.CreateListingProjectionOutboxMessage(privateListing.Id, "Upsert", 1003, false, now, generation.ActiveRunId));
            db.OutboxMessages.Add(SearchProjectionEvents.CreateListingProjectionOutboxMessage(deleted.Id, "Delete", 1004, false, now, generation.ActiveRunId));
            db.OutboxMessages.Add(SearchProjectionEvents.CreateAssetProjectionOutboxMessage(asset.Id, "Delete", 1005, now, generation.ActiveRunId));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri(MeiliUrl) };
        var client = new MeilisearchClient(http, MasterKey);
        await using var services = BuildSearchProvider();
        var backfill = new MeilisearchBackfillService(services.GetRequiredService<IServiceScopeFactory>(), client,
            Options.Create(_options), TimeProvider.System, NullLogger<MeilisearchBackfillService>.Instance);
        await backfill.BackfillAsync();
        Assert.Equal(1005, await _db.SearchBackfillReceipts.CountAsync());
        Assert.Equal(1005, await _db.OutboxMessages.CountAsync(m => m.PublishedAt == null));
        var run = await _db.SearchBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal(SearchBackfillRun.StatusCompleted, run.Status);
        Assert.NotNull(run.SwapTaskUid);
        Assert.True((await _store.GetGenerationAsync())!.IsReady);
        Assert.Empty((await _searchService.SearchPublicListingCandidatesAsync(new("camera"))).Value.CandidateIds);
        Assert.Contains(privateListing.Id, (await _searchService.SearchOwnerListingCandidatesAsync(new(privateListing.OwnerUserId, "Private"))).Value.CandidateIds);
        Assert.Contains(inactive.Id, (await _searchService.SearchOwnerListingCandidatesAsync(new(inactive.OwnerUserId, "Inactive"))).Value.CandidateIds);
        Assert.Empty((await _searchService.SearchOwnerListingCandidatesAsync(new(deleted.OwnerUserId, "Deleted"))).Value.CandidateIds);
        Assert.Empty((await _searchService.SearchOwnerAssetCandidatesAsync(new(inactive.OwnerUserId, "DELETED-SERIAL"))).Value.CandidateIds);
    }

    [Fact]
    public async Task Restart_AfterSwapSubmission_ReconcilesTaskWithoutSwappingAgain()
    {
        var id = Guid.NewGuid();
        var run = SearchBackfillRun.Create(id, $"staging_public_{id:N}", $"staging_owner_{id:N}", $"staging_assets_{id:N}", null, DateTimeOffset.UtcNow);
        await _provisioner.ConfigureIndexesAsync(run.StagingPublicIndex, run.StagingOwnerListingsIndex, run.StagingOwnerAssetsIndex);
        run.Transition(SearchBackfillRun.StatusSwapping, DateTimeOffset.UtcNow);
        var task = await _client.SwapIndexesAsync([
            new IndexSwap(_options.PublicListingsIndex, run.StagingPublicIndex),
            new IndexSwap(_options.OwnerListingsIndex, run.StagingOwnerListingsIndex),
            new IndexSwap(_options.OwnerAssetsIndex, run.StagingOwnerAssetsIndex)]);
        // Crash before persisting the UID: recover using the unique staging names in task history.
        var generation = (await _store.GetGenerationAsync())!;
        generation.MarkUnavailable(null, DateTimeOffset.UtcNow);
        generation.SetActiveRun(id, DateTimeOffset.UtcNow);
        await _store.SaveBackfillStateAsync(run, generation);
        await using var services = BuildSearchProvider();
        var backfill = new MeilisearchBackfillService(services.GetRequiredService<IServiceScopeFactory>(), _client, Options.Create(_options));
        await backfill.BackfillAsync();
        var saved = await _db.SearchBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal(task.TaskUid, saved.SwapTaskUid);
        Assert.Equal(SearchBackfillRun.StatusCompleted, saved.Status);
        Assert.True((await _store.GetGenerationAsync())!.IsReady);
    }

    private sealed class AfterFirstStagingUploadHandler(Func<Task> callback) : DelegatingHandler(new HttpClientHandler())
    {
        private bool _invoked;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_invoked && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("_staging_", StringComparison.Ordinal)
                && request.RequestUri.AbsolutePath.EndsWith("/documents", StringComparison.Ordinal))
            {
                _invoked = true;
                await callback();
            }
            return response;
        }
    }
}
