using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Availability;

public sealed class AvailabilityServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly TestTimeProvider _timeProvider = new(FixedNow);
    private readonly FakeAssetRepository _assetRepository = new();
    private readonly InventoryAvailabilityService _service;

    public AvailabilityServiceTests()
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = false,
            TtlSeconds = 60,
            TimeoutMs = 500
        });

        _service = new InventoryAvailabilityService(
            _assetRepository,
            NoOpInventoryAvailabilityCache.Instance,
            options,
            _timeProvider);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_NullRequest_ReturnsValidationError()
    {
        var result = await _service.CheckAvailabilityBatchAsync(null!);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_REQUEST", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_EmptyAssetIds_ReturnsValidationError()
    {
        var request = new CheckAvailabilityBatchRequest(new List<Guid>(), new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_ASSET_IDS", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_NullDates_ReturnsValidationError()
    {
        // 1. Both null
        var r1 = await _service.CheckAvailabilityBatchAsync(new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, null, null));
        Assert.True(r1.IsFailure);
        Assert.Equal("MISSING_DATE_PARAMETERS", r1.Error.Code);

        // 2. StartDate null
        var r2 = await _service.CheckAvailabilityBatchAsync(new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, null, new DateOnly(2026, 10, 12)));
        Assert.True(r2.IsFailure);
        Assert.Equal("MISSING_DATE_PARAMETERS", r2.Error.Code);

        // 3. EndDate null
        var r3 = await _service.CheckAvailabilityBatchAsync(new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, new DateOnly(2026, 10, 10), null));
        Assert.True(r3.IsFailure);
        Assert.Equal("MISSING_DATE_PARAMETERS", r3.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_MoreThan500AssetIds_ReturnsValidationError()
    {
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();
        var request = new CheckAvailabilityBatchRequest(ids, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsFailure);
        Assert.Equal("EXCEEDED_ASSET_IDS_LIMIT", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_Exactly500AssetIds_Succeeds()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();
        var request = new CheckAvailabilityBatchRequest(ids, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);
        Assert.Equal(500, result.Value.NotFoundAssetIds.Count);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_EmptyGuidInAssetIds_ReturnsValidationError()
    {
        var request = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid(), Guid.Empty }, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_ASSET_ID", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_InvalidDateOrder_ReturnsValidationError()
    {
        var request = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 10));
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_ExceededSixMonthsHorizon_ReturnsValidationError()
    {
        var start = new DateOnly(2026, 1, 1);
        var end = start.AddMonths(6).AddDays(1);
        var request = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, start, end);
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsFailure);
        Assert.Equal("EXCEEDED_HORIZON_LIMIT", result.Error.Code);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_SafeHorizonNearYear9999_DoesNotThrow()
    {
        // 9999-12-01 to 9999-12-02 must NOT throw ArgumentOutOfRangeException
        var start = new DateOnly(9999, 12, 1);
        var end = new DateOnly(9999, 12, 2);
        var request = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, start, end);
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_LeapYearHorizon_Succeeds()
    {
        var start = new DateOnly(2024, 2, 29);
        var end = new DateOnly(2024, 8, 29);
        var request = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, start, end);
        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_DeduplicatesAndPreservesFirstAppearanceOrder()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var asset1 = CreateAsset(id1, true);
        var asset3 = CreateAsset(id3, true);
        _assetRepository.Add(asset1);
        _assetRepository.Add(asset3);

        // id2 is not in repository (notFound)
        // input order: id1, id2, id1, id3, id2
        var request = new CheckAvailabilityBatchRequest(
            new[] { id1, id2, id1, id3, id2 },
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 12));

        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);

        var val = result.Value;
        Assert.Equal(new[] { id2 }, val.NotFoundAssetIds);
        Assert.Empty(val.UnavailableAssetIds);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_MixedOccupancyStates_CorrectClassification()
    {
        var windowStart = new DateOnly(2026, 10, 10);
        var windowEnd = new DateOnly(2026, 10, 15);
        var period = RentalPeriod.Create(windowStart, windowEnd).Value;

        // 1. Free asset
        var freeId = Guid.NewGuid();
        _assetRepository.Add(CreateAsset(freeId, true));

        // 2. Not accepting bookings -> Unavailable
        var noBookingId = Guid.NewGuid();
        _assetRepository.Add(CreateAsset(noBookingId, false));

        // 3. Has overlapping CalendarBlock -> Unavailable
        var blockedId = Guid.NewGuid();
        var blockedAsset = CreateAsset(blockedId, true);
        blockedAsset.CalendarBlocks.Add(CalendarBlock.Create(
            Guid.NewGuid(), blockedId, windowStart, windowEnd, CalendarBlockReason.Maintenance, "op", FixedNow, windowStart).Value);
        _assetRepository.Add(blockedAsset);

        // 4. Has active Hold overlapping -> Unavailable
        var heldActiveId = Guid.NewGuid();
        var heldActiveAsset = CreateAsset(heldActiveId, true);
        var activeHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), heldActiveId, period,
            FixedNow.AddMinutes(15), FixedNow, "SYS", "REF-ACT").Value;
        heldActiveAsset.Allocations.Add(activeHold);
        _assetRepository.Add(heldActiveAsset);

        // 5. Has expired Hold overlapping -> Not unavailable
        var heldExpiredId = Guid.NewGuid();
        var heldExpiredAsset = CreateAsset(heldExpiredId, true);
        var expiredHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), heldExpiredId, period,
            FixedNow.AddMinutes(-5), FixedNow.AddMinutes(-20), "SYS", "REF-EXP").Value;
        heldExpiredAsset.Allocations.Add(expiredHold);
        _assetRepository.Add(heldExpiredAsset);

        // 6. Has Allocated overlapping -> Unavailable
        var allocatedId = Guid.NewGuid();
        var allocatedAsset = CreateAsset(allocatedId, true);
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), allocatedId, period, FixedNow).Value;
        allocatedAsset.Allocations.Add(alloc);
        _assetRepository.Add(allocatedAsset);

        // 7. Has Released overlapping -> Not unavailable
        var releasedId = Guid.NewGuid();
        var releasedAsset = CreateAsset(releasedId, true);
        var rel = AssetAllocation.CreateManual(
            Guid.NewGuid(), releasedId, period, FixedNow).Value;
        rel.Release("Release for test", FixedNow.AddMinutes(5), "tester");
        releasedAsset.Allocations.Add(rel);
        _assetRepository.Add(releasedAsset);

        // 8. Has Cancelled overlapping -> Not unavailable
        var cancelledId = Guid.NewGuid();
        var cancelledAsset = CreateAsset(cancelledId, true);
        var can = AssetAllocation.CreateManual(
            Guid.NewGuid(), cancelledId, period, FixedNow).Value;
        can.Cancel("Cancel for test", FixedNow.AddMinutes(5), "tester");
        cancelledAsset.Allocations.Add(can);
        _assetRepository.Add(cancelledAsset);

        // 9. Missing in DB -> NotFound
        var missingId = Guid.NewGuid();

        var request = new CheckAvailabilityBatchRequest(
            new[] { freeId, noBookingId, blockedId, heldActiveId, heldExpiredId, allocatedId, releasedId, cancelledId, missingId },
            windowStart,
            windowEnd);

        var result = await _service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);

        var val = result.Value;
        Assert.Equal(new[] { noBookingId, blockedId, heldActiveId, allocatedId }, val.UnavailableAssetIds);
        Assert.Equal(new[] { missingId }, val.NotFoundAssetIds);
    }

    [Fact]
    public async Task CheckAvailabilityBatch_ClockAdvancementDuringIo_RecognizesHoldExpiry()
    {
        var windowStart = new DateOnly(2026, 10, 10);
        var windowEnd = new DateOnly(2026, 10, 12);

        var assetId = Guid.NewGuid();
        var asset = CreateAsset(assetId, true);
        // Hold expires 5 seconds after FixedNow
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, RentalPeriod.Create(windowStart, windowEnd).Value,
            FixedNow.AddSeconds(5), FixedNow, "SYS", "REF-MID").Value;
        asset.Allocations.Add(hold);

        // Custom repository that advances clock during snapshot query
        var delayedRepo = new DelayedSnapshotRepository(_assetRepository, () =>
        {
            // Advance clock past the hold expiry while snapshot is being read
            _timeProvider.Advance(TimeSpan.FromSeconds(10));
        });

        var options = Options.Create(new InventoryCacheOptions { Enabled = false });
        var serviceWithDelayedRepo = new InventoryAvailabilityService(delayedRepo, NoOpInventoryAvailabilityCache.Instance, options, _timeProvider);

        var request = new CheckAvailabilityBatchRequest(new[] { assetId }, windowStart, windowEnd);
        var result = await serviceWithDelayedRepo.CheckAvailabilityBatchAsync(request);

        Assert.True(result.IsSuccess);
        // Because clock is sampled after snapshot read, hold is expired and asset is NOT in UnavailableAssetIds!
        Assert.Empty(result.Value.UnavailableAssetIds);
    }

    [Fact]
    public async Task GetBusyRanges_ConfiguredFiveSecondsTtl_EnforcesFiveSeconds()
    {
        var assetId = Guid.NewGuid();
        var asset = CreateAsset(assetId, true);
        _assetRepository.Add(asset);

        var fakeCache = new InspectableFakeCache();
        var options = Options.Create(new InventoryCacheOptions { Enabled = true, TtlSeconds = 5 });
        var service = new InventoryAvailabilityService(_assetRepository, fakeCache, options, _timeProvider);

        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);

        // Verify deadline is exactly 5 seconds from snapshot start
        Assert.Single(fakeCache.SetCalls);
        var call = fakeCache.SetCalls[0];
        Assert.Equal(FixedNow.AddSeconds(5), call.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetBusyRanges_HoldExpiringInSubSecond_BoundsDeadlineBelowNormalTtl()
    {
        var assetId = Guid.NewGuid();
        var asset = CreateAsset(assetId, true);
        // Hold expires in 500 ms
        var subSecondExpiry = FixedNow.AddMilliseconds(500);
        asset.Allocations.Add(AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
            subSecondExpiry, FixedNow, "SYS", "REF-SUB").Value);
        _assetRepository.Add(asset);

        var fakeCache = new InspectableFakeCache();
        var options = Options.Create(new InventoryCacheOptions { Enabled = true, TtlSeconds = 60 });
        var service = new InventoryAvailabilityService(_assetRepository, fakeCache, options, _timeProvider);

        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);

        Assert.Single(fakeCache.SetCalls);
        var call = fakeCache.SetCalls[0];
        Assert.Equal(subSecondExpiry, call.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetBusyRanges_CachedMonthExpiresMidIo_ReloadsFromPostgresOnce()
    {
        var assetId = Guid.NewGuid();
        var asset = CreateAsset(assetId, true);
        asset.CalendarBlocks.Add(CalendarBlock.Create(
            Guid.NewGuid(), assetId, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8),
            CalendarBlockReason.Maintenance, "op", FixedNow, new DateOnly(2026, 10, 5)).Value);
        _assetRepository.Add(asset);

        // Fake cache where month expires during processing
        var midIoExpiringCache = new MidIoExpiringCache(FixedNow.AddSeconds(2), () =>
        {
            // Advance clock past cache expiration right before return check
            _timeProvider.Advance(TimeSpan.FromSeconds(5));
        });

        var options = Options.Create(new InventoryCacheOptions { Enabled = true, TtlSeconds = 60 });
        var service = new InventoryAvailabilityService(_assetRepository, midIoExpiringCache, options, _timeProvider);

        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);
        // Proves reload succeeded and returned valid ranges
        Assert.Single(result.Value.Ranges);
        Assert.Equal(new DateOnly(2026, 10, 5), result.Value.Ranges[0].StartDate);
        Assert.Equal(new DateOnly(2026, 10, 8), result.Value.Ranges[0].EndDate);
    }

    [Fact]
    public async Task GetBusyRanges_MergeRanges_DayNumberPreventsOverflowAtYear9999()
    {
        var ranges = new (DateOnly Start, DateOnly End)[]
        {
            (new DateOnly(9999, 12, 30), new DateOnly(9999, 12, 31))
        };

        // MergeRanges uses DayNumber and does not throw ArgumentOutOfRangeException
        var merged = InventoryAvailabilityService.MergeRanges(ranges);
        Assert.Single(merged);
        Assert.Equal(new DateOnly(9999, 12, 30), merged[0].StartDate);
        Assert.Equal(new DateOnly(9999, 12, 31), merged[0].EndDate);
    }

    private static InventoryAsset CreateAsset(Guid id, bool acceptingBookings)
    {
        var rate = AssetRentalRate.Create(id, 100_000m, null, null, "VND", FixedNow).Value;
        var asset = InventoryAsset.Create(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3",
            1,
            "{}",
            FixedNow,
            rate).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Disabled for test", FixedNow);
        }

        return asset;
    }

    private sealed class DelayedSnapshotRepository : IInventoryAssetRepository
    {
        private readonly IInventoryAssetRepository _inner;
        private readonly Action _onSnapshot;

        public DelayedSnapshotRepository(IInventoryAssetRepository inner, Action onSnapshot)
        {
            _inner = inner;
            _onSnapshot = onSnapshot;
        }

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetByIdAsync(id, cancellationToken);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.SearchForOwnerAsync(criteria, cancellationToken);

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.CountForOwnerAsync(criteria, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.GetDistinctSchemaVersionsForOwnerAsync(criteria, cancellationToken);

        public Task<InventoryAsset?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetPublicByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            _inner.GetDistinctSchemaVersionsPublicAsync(filters, cancellationToken);

        public Task<PublicAssetSearchResult> SearchPublicAsync(PublicAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.SearchPublicAsync(criteria, cancellationToken);
        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            _inner.GetCalendarSnapshotAsync(assetId, startDate, endDate, cancellationToken);

        public async Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            var res = await _inner.GetAvailabilitySnapshotsAsync(assetIds, startDate, endDate, cancellationToken);
            _onSnapshot();
            return res;
        }

        public void Add(InventoryAsset asset) => _inner.Add(asset);
        public void Update(InventoryAsset asset) => _inner.Update(asset);
    }

    private sealed class InspectableFakeCache : IInventoryAvailabilityCache
    {
        public bool IsEnabled => true;
        public List<(Guid AssetId, string MonthKey, IReadOnlyList<BusyRangeDto> Ranges, DateTimeOffset ExpiresAtUtc)> SetCalls { get; } = new();

        public Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(Guid assetId, IReadOnlyList<string> monthKeys, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, CachedMonthRanges?> res = monthKeys.ToDictionary(k => k, _ => (CachedMonthRanges?)null);
            return Task.FromResult(res);
        }

        public Task SetMonthRangesAsync(Guid assetId, string monthKey, IReadOnlyList<BusyRangeDto> ranges, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        {
            SetCalls.Add((assetId, monthKey, ranges, expiresAtUtc));
            return Task.CompletedTask;
        }

        public Task InvalidateAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MidIoExpiringCache : IInventoryAvailabilityCache
    {
        private readonly DateTimeOffset _expiry;
        private readonly Action _beforeNextOperation;

        public bool IsEnabled => true;

        public MidIoExpiringCache(DateTimeOffset expiry, Action beforeNextOperation)
        {
            _expiry = expiry;
            _beforeNextOperation = beforeNextOperation;
        }

        public Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(Guid assetId, IReadOnlyList<string> monthKeys, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
            var hit = new CachedMonthRanges(ranges, _expiry);
            IReadOnlyDictionary<string, CachedMonthRanges?> res = monthKeys.ToDictionary(k => k, _ => (CachedMonthRanges?)hit);

            // Trigger mid-IO clock advance right after returning hit
            _beforeNextOperation();

            return Task.FromResult(res);
        }

        public Task SetMonthRangesAsync(Guid assetId, string monthKey, IReadOnlyList<BusyRangeDto> ranges, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAssetRepository : IInventoryAssetRepository
    {
        public readonly Dictionary<Guid, InventoryAsset> Store = new();

        public Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) ? a : null);

        public Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.OwnerUserId == ownerUserId ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> SearchForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => a.OwnerUserId == criteria.OwnerUserId).Skip(criteria.Skip).Take(criteria.Take).ToList());

        public Task<int> CountForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Values.Count(a => a.OwnerUserId == criteria.OwnerUserId));

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsForOwnerAsync(OwnerAssetSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Store.Values.Where(a => a.OwnerUserId == criteria.OwnerUserId).Select(a => a.SchemaVersion).Distinct().OrderBy(v => v).ToList());

        public Task<InventoryAsset?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var a) && a.AcceptingBookings ? a : null);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(Store.Values.Where(a => a.AcceptingBookings).Select(a => a.SchemaVersion).Distinct().OrderBy(v => v).ToList());

        public Task<PublicAssetSearchResult> SearchPublicAsync(PublicAssetSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            var matching = Store.Values.Where(a => a.AcceptingBookings).ToList();
            var paged = matching.Skip(criteria.Skip).Take(criteria.Take).ToList();
            return Task.FromResult(new PublicAssetSearchResult(paged, matching.Count));
        }
        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(assetId, out var a) ? a : null);

        public Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryAsset>>(Store.Values.Where(a => assetIds.Contains(a.Id)).ToList());

        public void Add(InventoryAsset asset) => Store[asset.Id] = asset;
        public void Update(InventoryAsset asset) => Store[asset.Id] = asset;
    }
}
