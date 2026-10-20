using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;

namespace Dprental.InventoryService.Application.Search.Adapters;

public sealed partial class MeilisearchBackfillService
{
    private async Task<(int Public, int Listings, int Assets)> LoadSnapshotAsync(SearchBackfillRun run,
        ISearchProjectionStore store, CancellationToken ct)
    {
        int publicCount = 0, listingCount = 0, assetCount = 0;
        Guid? cursor = null;
        while (true)
        {
            var rows = await store.ReadListingsAsync(cursor, BatchSize, cancellationToken: ct);
            if (rows.Count == 0) break;
            await using (var writer = await AcquireAsync(store, SearchCoordination.WriterKey, ct))
                await UploadListingsAsync(run, rows, [], ct);
            listingCount += rows.Count;
            publicCount += rows.Count(l => l.Status == EquipmentListingStatus.Active);
            cursor = rows[^1].Id;
            run.AdvanceListings(cursor, rows.Count < BatchSize, _clock.GetUtcNow());
            await store.SaveBackfillRunAsync(run, ct);
            if (rows.Count < BatchSize) break;
        }
        run.AdvanceListings(cursor, true, _clock.GetUtcNow());
        cursor = null;
        while (true)
        {
            var rows = await store.ReadAssetsAsync(cursor, BatchSize, cancellationToken: ct);
            if (rows.Count == 0) break;
            await using (var writer = await AcquireAsync(store, SearchCoordination.WriterKey, ct))
                await UploadAssetsAsync(run, rows, [], ct);
            assetCount += rows.Count;
            cursor = rows[^1].Id;
            run.AdvanceAssets(cursor, rows.Count < BatchSize, _clock.GetUtcNow());
            await store.SaveBackfillRunAsync(run, ct);
            if (rows.Count < BatchSize) break;
        }
        run.AdvanceAssets(cursor, true, _clock.GetUtcNow());
        await store.SaveBackfillRunAsync(run, ct);
        return (publicCount, listingCount, assetCount);
    }

    private async Task UploadListingsAsync(SearchBackfillRun run, List<EquipmentListing> listings,
        List<Guid> deleted, CancellationToken ct)
    {
        var owners = client.Index(run.StagingOwnerListingsIndex);
        var publicIndex = client.Index(run.StagingPublicIndex);
        if (listings.Count > 0)
        {
            var ownerTask = await owners.AddDocumentsAsync(listings.Select(SearchProjectionMapper.ToOwnerListingDocument), "id", ct);
            await WaitAsync(ownerTask.TaskUid, ct);
            var active = listings.Where(l => l.Status == EquipmentListingStatus.Active)
                .Select(SearchProjectionMapper.ToPublicListingDocument).ToList();
            if (active.Count > 0)
            { var task = await publicIndex.AddDocumentsAsync(active, "id", ct); await WaitAsync(task.TaskUid, ct); }
        }
        var privateIds = deleted.Select(id => id.ToString("D")).ToList();
        if (privateIds.Count > 0)
        { var task = await owners.DeleteDocumentsAsync(privateIds, ct); await WaitAsync(task.TaskUid, ct); }
        var publicIds = privateIds.Concat(listings.Where(l => l.Status != EquipmentListingStatus.Active)
            .Select(l => l.Id.ToString("D"))).Distinct().ToList();
        if (publicIds.Count > 0)
        { var task = await publicIndex.DeleteDocumentsAsync(publicIds, ct); await WaitAsync(task.TaskUid, ct); }
    }

    private async Task UploadAssetsAsync(SearchBackfillRun run, List<InventoryAsset> assets,
        List<Guid> deleted, CancellationToken ct)
    {
        var index = client.Index(run.StagingOwnerAssetsIndex);
        if (assets.Count > 0)
        { var task = await index.AddDocumentsAsync(assets.Select(SearchProjectionMapper.ToOwnerAssetDocument), "id", ct); await WaitAsync(task.TaskUid, ct); }
        if (deleted.Count > 0)
        { var task = await index.DeleteDocumentsAsync(deleted.Select(id => id.ToString("D")), ct); await WaitAsync(task.TaskUid, ct); }
    }
}
