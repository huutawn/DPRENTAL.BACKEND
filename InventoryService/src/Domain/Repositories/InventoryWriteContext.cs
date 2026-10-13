using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai IInventoryWriteContext cung cấp các repository dùng chung một scoped DbContext trong giao dịch.
/// </summary>
public sealed class InventoryWriteContext : IInventoryWriteContext
{
    private readonly InventoryServiceDbContext _context;

    public InventoryWriteContext(
        InventoryServiceDbContext context,
        Guid assetId,
        Guid? listingId,
        IEquipmentListingRepository listings,
        IInventoryAssetRepository assets,
        IAssetAllocationRepository allocations,
        ICalendarBlockRepository calendarBlocks,
        IEnumerable<Guid>? initiallyLockedAssets = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        AssetId = assetId;
        ListingId = listingId;
        Listings = listings ?? throw new ArgumentNullException(nameof(listings));
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        CalendarBlocks = calendarBlocks ?? throw new ArgumentNullException(nameof(calendarBlocks));

        if (initiallyLockedAssets != null)
        {
            foreach (var id in initiallyLockedAssets)
            {
                if (id != Guid.Empty)
                {
                    LockedAssetIds.Add(id);
                }
            }
        }
        else if (assetId != Guid.Empty)
        {
            LockedAssetIds.Add(assetId);
        }
    }

    /// <inheritdoc />
    public Guid AssetId { get; }

    /// <inheritdoc />
    public Guid? ListingId { get; }

    /// <inheritdoc />
    public IEquipmentListingRepository Listings { get; }

    /// <inheritdoc />
    public IInventoryAssetRepository Assets { get; }

    /// <inheritdoc />
    public IAssetAllocationRepository Allocations { get; }

    /// <inheritdoc />
    public ICalendarBlockRepository CalendarBlocks { get; }

    public HashSet<Guid> LockedAssetIds { get; } = new();

    /// <inheritdoc />
    public async Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);

        var sortedIds = assetIds.Distinct().OrderBy(id => id).ToList();
        foreach (var id in sortedIds)
        {
            if (id == Guid.Empty || LockedAssetIds.Contains(id))
            {
                continue;
            }

            await _context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0});",
                [AssetLockKey.FromAssetId(id)],
                cancellationToken);

            LockedAssetIds.Add(id);
        }
    }

    /// <inheritdoc />
    public void StageOutbox(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.OutboxMessages.Add(message);
    }

    private static long _localRevisionSeq = 1000;

    /// <inheritdoc />
    public async Task<long> GetNextSearchRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            return await _context.Database
                .SqlQueryRaw<long>("SELECT nextval('inventory.search_projection_revision_seq') AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        return Interlocked.Increment(ref _localRevisionSeq);
    }
}
