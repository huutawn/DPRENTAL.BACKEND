using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

public sealed partial class SearchProjectionStore
{
    public async Task<IAsyncDisposable?> TryAcquireCoordinationAsync(long key, CancellationToken cancellationToken = default) =>
        await SearchCoordination.TryAcquireAsync(context, key, cancellationToken);

    public Task<List<EquipmentListing>> ReadListingsAsync(Guid? cursor, int limit, IReadOnlyList<Guid>? ids = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.EquipmentListings.AsNoTracking();
        if (cursor.HasValue) query = query.Where(l => l.Id > cursor.Value);
        if (ids is not null) query = query.Where(l => ids.Contains(l.Id));
        return query.OrderBy(l => l.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<List<InventoryAsset>> ReadAssetsAsync(Guid? cursor, int limit, IReadOnlyList<Guid>? ids = null,
        IReadOnlyList<Guid>? listingIds = null, CancellationToken cancellationToken = default)
    {
        var query = context.Assets.Include(a => a.Listing).AsNoTracking();
        if (cursor.HasValue) query = query.Where(a => a.Id > cursor.Value);
        if (ids is not null) query = query.Where(a => ids.Contains(a.Id));
        if (listingIds is not null) query = query.Where(a => listingIds.Contains(a.EquipmentListingId));
        return query.OrderBy(a => a.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<List<OutboxMessage>> ReadBackfillJournalAsync(Guid runId, string topic, int limit,
        CancellationToken cancellationToken = default) => context.OutboxMessages.FromSqlInterpolated($"""
            SELECT m.* FROM inventory.outbox_messages m
            WHERE m.topic = {topic} AND m.payload->'payload'->>'runId' = {runId.ToString("D")}
                AND NOT EXISTS (SELECT 1 FROM inventory.search_backfill_receipts r
                                WHERE r.run_id = {runId} AND r.message_id = m.id)
            ORDER BY m.created_at, m.id LIMIT {limit}
            """).AsNoTracking().ToListAsync(cancellationToken);

    public async Task RecordBackfillReceiptsAsync(Guid runId, IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        context.SearchBackfillReceipts.AddRange(messageIds.Select(id => new SearchBackfillReceipt
        { Id = Guid.NewGuid(), RunId = runId, MessageId = id }));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveBackfillStateAsync(SearchBackfillRun run, SearchGeneration generation,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await SaveBackfillRunAsync(run, cancellationToken);
        await SaveGenerationAsync(generation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<bool> HasUncertainSubmissionsAsync(CancellationToken cancellationToken = default) =>
        context.SearchProjectionJobs.AnyAsync(j => j.SubmissionUncertain, cancellationToken);

    public async Task<List<int>> ReadPendingTaskIdsAsync(CancellationToken cancellationToken = default)
    {
        var arrays = await context.SearchProjectionJobs.AsNoTracking().Where(j => j.PendingTaskUids.Length > 0)
            .Select(j => j.PendingTaskUids).ToListAsync(cancellationToken);
        return arrays.SelectMany(a => a).Distinct().ToList();
    }
}
