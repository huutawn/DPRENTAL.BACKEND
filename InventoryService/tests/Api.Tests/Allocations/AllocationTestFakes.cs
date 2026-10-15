using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Api.Tests.Allocations;

internal static class AllocationTestConstants
{
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);
}

internal sealed class TestCaller : IInventoryCallerContext
{
    public bool IsAuthenticated { get; set; } = true;
    public bool IsService { get; set; } = true;
    public bool IsAdmin { get; set; }
    public Guid? UserId { get; set; }
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string> { InventoryAccessEvaluator.ScopeAllocationWrite };
    public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
    public Guid? OrganizationId { get; set; }
}

internal sealed class FakeListingRepository : IEquipmentListingRepository
{
    public readonly Dictionary<Guid, EquipmentListing> Store = new();

    public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

    public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);

    public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);
    public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        var list = Store.Values
            .Where(l => ids.Contains(l.Id))
            .Select(l => new EquipmentListingBatchItem(l.Id, l.OwnerUserId, l.Name, l.ThumbnailMediaId, l.DailyRate, l.Currency, l.Status))
            .ToList();
        return Task.FromResult<IReadOnlyList<EquipmentListingBatchItem>>(list);
    }
    public Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(EquipmentListingStatisticsCriteria criteria, CancellationToken cancellationToken = default)
    {
        var q = Store.Values.AsEnumerable();
        if (criteria.OwnerUserId.HasValue)
        {
            q = q.Where(l => l.OwnerUserId == criteria.OwnerUserId.Value);
        }
        if (criteria.Status.HasValue)
        {
            q = q.Where(l => l.Status == criteria.Status.Value);
        }
        var byType = q.GroupBy(l => l.EquipmentTypeId).ToDictionary(g => g.Key, g => g.Count());
        var byBrand = q.GroupBy(l => l.BrandId).ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(new EquipmentListingStatisticsResult(byType, byBrand));
    }

    public Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(OwnerEquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        var q = Store.Values.Where(l => l.OwnerUserId == criteria.OwnerUserId);
        if (criteria.EquipmentTypeId.HasValue)
        {
            q = q.Where(l => l.EquipmentTypeId == criteria.EquipmentTypeId.Value);
        }
        if (criteria.BrandId.HasValue)
        {
            q = q.Where(l => l.BrandId == criteria.BrandId.Value);
        }
        if (criteria.Status.HasValue)
        {
            q = q.Where(l => l.Status == criteria.Status.Value);
        }
        if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
        {
            q = q.Where(l => l.Name.Contains(criteria.SearchKeyword!, StringComparison.OrdinalIgnoreCase) ||
                             l.Model.Contains(criteria.SearchKeyword!, StringComparison.OrdinalIgnoreCase));
        }
        var list = q.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).ToList();
        var items = list.Skip(criteria.Skip).Take(criteria.Take)
            .Select(l => new OwnerEquipmentListingItem(l, l.Assets.Count(a => a.AcceptingBookings)))
            .ToList();
        return Task.FromResult(new OwnerEquipmentListingSearchResult(items, list.Count));
    }

    public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

    public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublicEquipmentListingSearchResult(Array.Empty<PublicEquipmentListingItem>(), 0));

    public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(listingId, out var l) ? l : null);

    public void Add(EquipmentListing listing) => Store[listing.Id] = listing;
    public void Update(EquipmentListing listing) => Store[listing.Id] = listing;
}

internal sealed class FakeAssetRepository : IInventoryAssetRepository
{
    public readonly Dictionary<Guid, InventoryAsset> Store = new();

    public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

    public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) && a.Listing?.OwnerUserId == ownerUserId ? a : null);

    public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.Listing?.OwnerUserId == criteria.OwnerUserId).Skip(criteria.Skip).Take(criteria.Take).ToList());

    public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.Values.Count(a => a.Listing?.OwnerUserId == criteria.OwnerUserId));

    public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

    public Task<IReadOnlyList<InventoryAsset>> GetAssetsByListingIdAsync(Guid listingId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.EquipmentListingId == listingId).Skip(skip).Take(take).ToList());

    public Task<int> CountAssetsByListingIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.Values.Count(a => a.EquipmentListingId == listingId));

    public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

    public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

    public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
    public Task<bool> ExistsSerialInListingAsync(Guid listingId, string serialNumber, Guid? excludeAssetId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.Values.Any(a => a.EquipmentListingId == listingId &&
            (!excludeAssetId.HasValue || a.Id != excludeAssetId.Value) &&
            string.Equals(a.SerialNumber.Trim(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase)));
    public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
}

internal sealed class FakeAllocationRepository : IAssetAllocationRepository
{
    public readonly Dictionary<Guid, AssetAllocation> Store = new();

    public Task<AssetAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

    public Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) && a.Asset?.Listing?.OwnerUserId == ownerUserId ? a : null);

    public Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default)
    {
        var alloc = Store.Values.FirstOrDefault(a =>
            string.Equals(a.SourceSystem, sourceSystem.Trim(), StringComparison.Ordinal) &&
            string.Equals(a.ExternalReference, externalReference.Trim(), StringComparison.Ordinal));
        return Task.FromResult(alloc);
    }

    public Task<IReadOnlyList<AssetAllocation>> GetByGroupReferenceAsync(string sourceSystem, string groupReference, CancellationToken cancellationToken = default)
    {
        var list = Store.Values.Where(a =>
            string.Equals(a.SourceSystem, sourceSystem.Trim(), StringComparison.Ordinal) &&
            string.Equals(a.GroupReference, groupReference.Trim(), StringComparison.Ordinal))
            .OrderBy(a => a.AssetId)
            .ToList();
        return Task.FromResult<IReadOnlyList<AssetAllocation>>(list);
    }

    public Task<IReadOnlyList<AssetAllocation>> GetActiveCandidatesAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var list = Store.Values
            .Where(a => a.AssetId == assetId &&
                        (a.Status == AllocationStatus.Held || a.Status == AllocationStatus.Allocated) &&
                        a.RentalPeriod.StartDate <= endDate &&
                        a.RentalPeriod.EndDate >= startDate)
            .ToList();
        return Task.FromResult<IReadOnlyList<AssetAllocation>>(list);
    }

    public Task<IReadOnlyList<AssetAllocation>> GetExpiredHoldsAsync(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var list = Store.Values
            .Where(a => a.AssetId == assetId &&
                        a.Status == AllocationStatus.Held &&
                        a.HoldExpiresAt.HasValue &&
                        a.HoldExpiresAt.Value <= now)
            .ToList();
        return Task.FromResult<IReadOnlyList<AssetAllocation>>(list);
    }

    public Task<IReadOnlyList<AssetAllocation>> ListByAssetAsync(Guid assetId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AssetAllocation>>(Store.Values.Where(a => a.AssetId == assetId).ToList());

    public Task<IReadOnlyList<AssetAllocation>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AssetAllocation>>(Store.Values.Where(a => a.Asset?.Listing?.OwnerUserId == ownerUserId).ToList());

    public void Add(AssetAllocation allocation) => Store[allocation.Id] = allocation;
    public void AddRange(IEnumerable<AssetAllocation> allocations)
    {
        foreach (var a in allocations) Store[a.Id] = a;
    }
    public void Update(AssetAllocation allocation) => Store[allocation.Id] = allocation;
}

internal sealed class FakeCalendarBlockRepository : ICalendarBlockRepository
{
    public readonly Dictionary<Guid, CalendarBlock> Store = new();

    public Task<CalendarBlock?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var b) ? b : null);

    public Task<IReadOnlyList<CalendarBlock>> GetOverlappingBlocksAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var list = Store.Values
            .Where(b => b.AssetId == assetId && b.Overlaps(startDate, endDate))
            .ToList();
        return Task.FromResult<IReadOnlyList<CalendarBlock>>(list);
    }

    public Task<IReadOnlyList<CalendarBlock>> ListByAssetInHorizonAsync(Guid assetId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CalendarBlock>>(Store.Values.Where(b => b.AssetId == assetId).ToList());

    public Task<IReadOnlyList<CalendarBlock>> ListByAssetAsync(Guid assetId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CalendarBlock>>(Store.Values.Where(b => b.AssetId == assetId).Skip(skip).Take(take).ToList());

    public void Add(CalendarBlock block) => Store[block.Id] = block;
    public void Remove(CalendarBlock block) => Store.Remove(block.Id);
}

internal sealed class FakeWriteContext : IInventoryWriteContext
{
    public Guid AssetId { get; }
    public Guid? ListingId { get; }
    public IEquipmentListingRepository Listings { get; set; } = new FakeListingRepository();
    public IInventoryAssetRepository Assets { get; }
    public IAssetAllocationRepository Allocations { get; }
    public ICalendarBlockRepository CalendarBlocks { get; }
    public List<OutboxMessage> StagedOutbox { get; } = new();
    public Guid? SearchBackfillRunId => null;

    public Task<long> GetNextSearchRevisionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(1L);

    public FakeWriteContext(
        Guid assetId,
        IInventoryAssetRepository assets,
        IAssetAllocationRepository allocations,
        ICalendarBlockRepository calendarBlocks,
        Guid? listingId = null,
        IEquipmentListingRepository? listings = null)
    {
        AssetId = assetId;
        ListingId = listingId;
        Assets = assets;
        Allocations = allocations;
        CalendarBlocks = calendarBlocks;
        if (listings != null) Listings = listings;
    }

    public Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void StageOutbox(OutboxMessage message) => StagedOutbox.Add(message);
}

internal sealed class FakeWriteTransaction : IInventoryWriteTransaction
{
    private readonly FakeAssetRepository _assets;
    private readonly FakeAllocationRepository _allocations;
    private readonly FakeCalendarBlockRepository _calendarBlocks;
    public readonly FakeListingRepository Listings = new();
    public readonly List<OutboxMessage> StagedOutbox = new();

    public Func<Guid, Task<Result?>>? InterceptBeforeOperation { get; set; }

    public FakeWriteTransaction(
        FakeAssetRepository assets,
        FakeAllocationRepository allocations,
        FakeCalendarBlockRepository calendarBlocks)
    {
        _assets = assets;
        _allocations = allocations;
        _calendarBlocks = calendarBlocks;
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Guid assetId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        if (InterceptBeforeOperation is not null)
        {
            var interceptResult = await InterceptBeforeOperation(assetId);
            if (interceptResult is not null && interceptResult.IsFailure)
            {
                return interceptResult.Error;
            }
        }

        var ctx = new FakeWriteContext(assetId, _assets, _allocations, _calendarBlocks, listings: Listings);
        var result = await operation(ctx, cancellationToken);
        if (result.IsSuccess)
        {
            StagedOutbox.AddRange(ctx.StagedOutbox);
        }
        return result;
    }

    public async Task<Result<T>> ExecuteListingWriteAsync<T>(
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        var ctx = new FakeWriteContext(Guid.Empty, _assets, _allocations, _calendarBlocks, listingId: listingId, listings: Listings);
        var result = await operation(ctx, cancellationToken);
        if (result.IsSuccess)
        {
            StagedOutbox.AddRange(ctx.StagedOutbox);
        }
        return result;
    }

    public async Task<Result<T>> ExecuteGroupHoldAsync<T>(
        string sourceSystem,
        string groupReference,
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        var ctx = new FakeWriteContext(Guid.Empty, _assets, _allocations, _calendarBlocks, listingId: listingId, listings: Listings);
        var result = await operation(ctx, cancellationToken);
        if (result.IsSuccess)
        {
            StagedOutbox.AddRange(ctx.StagedOutbox);
        }
        return result;
    }
}

internal static class AllocationTestHelper
{
    public static InventoryAsset CreateAsset(Guid assetId, bool acceptingBookings = true, Guid? listingId = null, Guid? ownerUserId = null)
    {
        var actualListingId = listingId ?? Guid.NewGuid();
        var actualOwner = ownerUserId ?? Guid.NewGuid();
        var listing = EquipmentListing.Create(
            actualListingId,
            actualOwner,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3",
            null,
            "Sony FX3",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            AllocationTestConstants.FixedNow,
            2024).Value;

        listing.UpdateStatus(EquipmentListingStatus.Active, true, AllocationTestConstants.FixedNow);
        var asset = InventoryAsset.Create(
            assetId,
            actualListingId,
            $"SN-{assetId:N}",
            AllocationTestConstants.FixedNow).Value;

        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended", AllocationTestConstants.FixedNow);
        }
        return asset;
    }
}
