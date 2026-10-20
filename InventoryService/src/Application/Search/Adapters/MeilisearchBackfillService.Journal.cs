using System.Text.Json;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Search.Adapters;

public sealed partial class MeilisearchBackfillService
{
    private async Task ReplayJournalAsync(SearchBackfillRun run, ISearchProjectionStore store,
        CancellationToken ct, bool writerHeld = false)
    {
        while (true)
        {
            // Receipts, not a timestamp/sequence cursor, also pick up transactions committed out of order.
            var messages = await store.ReadBackfillJournalAsync(run.RunId, KafkaTopics.InventoryProjectionEventsV1, BatchSize, ct);
            if (messages.Count == 0) return;
            await using var writer = writerHeld ? null : await AcquireAsync(store, SearchCoordination.WriterKey, ct);
            var payloads = messages.Select(m =>
            {
                using var document = JsonDocument.Parse(m.Payload);
                var payload = JsonSerializer.Deserialize<SearchProjectionChangedPayload>(document.RootElement.GetProperty("payload").GetRawText());
                if (payload is null || payload.EntityId == Guid.Empty || payload.Revision <= 0 ||
                    payload.EntityType is not ("EquipmentListing" or "InventoryAsset") || payload.Action is not ("Upsert" or "Delete"))
                    throw new InvalidOperationException("Invalid committed search journal event.");
                return payload;
            }).ToList();
            var listingIds = payloads.Where(p => p.EntityType == "EquipmentListing").Select(p => p.EntityId).Distinct().ToList();
            var listings = await store.ReadListingsAsync(null, BatchSize, listingIds, ct);
            await UploadListingsAsync(run, listings, listingIds.Except(listings.Select(l => l.Id)).ToList(), ct);
            var assetIds = payloads.Where(p => p.EntityType == "InventoryAsset").Select(p => p.EntityId).Distinct().ToList();
            var assets = await store.ReadAssetsAsync(null, BatchSize, assetIds, cancellationToken: ct);
            await UploadAssetsAsync(run, assets, assetIds.Except(assets.Select(a => a.Id)).ToList(), ct);
            var fanoutIds = payloads.Where(p => p.EntityType == "EquipmentListing" && p.NeedsAssetFanout)
                .Select(p => p.EntityId).Distinct().ToList();
            Guid? cursor = null;
            while (fanoutIds.Count > 0)
            {
                var batch = await store.ReadAssetsAsync(cursor, BatchSize, listingIds: fanoutIds, cancellationToken: ct);
                if (batch.Count == 0) break;
                await UploadAssetsAsync(run, batch, [], ct);
                cursor = batch[^1].Id;
                if (batch.Count < BatchSize) break;
            }
            await store.RecordBackfillReceiptsAsync(run.RunId, messages.Select(m => m.Id).ToList(), ct);
        }
    }
}
