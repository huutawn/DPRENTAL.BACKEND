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

internal sealed class FakeAssetRepository : IInventoryAssetRepository
{
    public readonly Dictionary<Guid, InventoryAsset> Store = new();

    public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

    public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) && a.OwnerUserId == ownerUserId ? a : null);

    public Task<IReadOnlyList<InventoryAsset>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.OwnerUserId == ownerUserId).ToList());

    public Task<int> CountByOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.Values.Count(a => a.OwnerUserId == ownerUserId));

    public Task<IReadOnlyList<InventoryAsset>> ListAllAsync(int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.ToList());

    public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

    public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

    public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
    public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
}

internal sealed class FakeAllocationRepository : IAssetAllocationRepository
{
    public readonly Dictionary<Guid, AssetAllocation> Store = new();

    public Task<AssetAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

    public Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store.TryGetValue(id, out var a) && a.Asset.OwnerUserId == ownerUserId ? a : null);

    public Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default)
    {
        var alloc = Store.Values.FirstOrDefault(a =>
            string.Equals(a.SourceSystem, sourceSystem.Trim(), StringComparison.Ordinal) &&
            string.Equals(a.ExternalReference, externalReference.Trim(), StringComparison.Ordinal));
        return Task.FromResult(alloc);
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
        Task.FromResult<IReadOnlyList<AssetAllocation>>(Store.Values.Where(a => a.Asset.OwnerUserId == ownerUserId).ToList());

    public void Add(AssetAllocation allocation) => Store[allocation.Id] = allocation;
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
    public IInventoryAssetRepository Assets { get; }
    public IAssetAllocationRepository Allocations { get; }
    public ICalendarBlockRepository CalendarBlocks { get; }
    public List<OutboxMessage> StagedOutbox { get; } = new();

    public FakeWriteContext(
        Guid assetId,
        IInventoryAssetRepository assets,
        IAssetAllocationRepository allocations,
        ICalendarBlockRepository calendarBlocks)
    {
        AssetId = assetId;
        Assets = assets;
        Allocations = allocations;
        CalendarBlocks = calendarBlocks;
    }

    public void StageOutbox(OutboxMessage message) => StagedOutbox.Add(message);
}

internal sealed class FakeWriteTransaction : IInventoryWriteTransaction
{
    private readonly FakeAssetRepository _assets;
    private readonly FakeAllocationRepository _allocations;
    private readonly FakeCalendarBlockRepository _calendarBlocks;
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

        var ctx = new FakeWriteContext(assetId, _assets, _allocations, _calendarBlocks);
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
    public static InventoryAsset CreateAsset(Guid assetId, bool acceptingBookings = true)
    {
        var asset = InventoryAsset.Create(
            assetId, Guid.NewGuid(), Guid.NewGuid(), "Sony FX3", 1, "{}", AllocationTestConstants.FixedNow).Value;
        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended", AllocationTestConstants.FixedNow);
        }
        return asset;
    }
}
