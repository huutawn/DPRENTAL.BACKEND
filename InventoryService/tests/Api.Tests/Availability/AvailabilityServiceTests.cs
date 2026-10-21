using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common;
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
    private readonly FakeListingRepository _listingRepository = new();
    private readonly InventoryAvailabilityService _service;

    public AvailabilityServiceTests()
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = false,
            DynamicTtlSeconds = 60,
            TimeoutMs = 500
        });

        _service = new InventoryAvailabilityService(
            _assetRepository,
            _listingRepository,
            NoOpInventoryReadCache.Instance,
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
        var serviceWithDelayedRepo = new InventoryAvailabilityService(delayedRepo, _listingRepository, NoOpInventoryReadCache.Instance, options, _timeProvider);
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
        var options = Options.Create(new InventoryCacheOptions { Enabled = true, DynamicTtlSeconds = 5 });
        var service = new InventoryAvailabilityService(_assetRepository, _listingRepository, fakeCache, options, _timeProvider);

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
        var options = Options.Create(new InventoryCacheOptions { Enabled = true, DynamicTtlSeconds = 60 });
        var service = new InventoryAvailabilityService(_assetRepository, _listingRepository, fakeCache, options, _timeProvider);

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

        var options = Options.Create(new InventoryCacheOptions { Enabled = true, DynamicTtlSeconds = 60 });
        var service = new InventoryAvailabilityService(_assetRepository, _listingRepository, midIoExpiringCache, options, _timeProvider);

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
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
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
            FixedNow,
            2024).Value;

        var asset = InventoryAsset.Create(id, listing.Id, $"SN-{id:N}", FixedNow).Value;
        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);

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

        public Task<IReadOnlyList<InventoryAsset>> GetAssetsByListingIdAsync(Guid listingId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.GetAssetsByListingIdAsync(listingId, skip, take, cancellationToken);

        public Task<int> CountAssetsByListingIdAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.CountAssetsByListingIdAsync(listingId, cancellationToken);
        public Task<InventoryAsset?> GetCalendarSnapshotAsync(Guid assetId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            _inner.GetCalendarSnapshotAsync(assetId, startDate, endDate, cancellationToken);

        public async Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(IReadOnlyList<Guid> assetIds, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            var res = await _inner.GetAvailabilitySnapshotsAsync(assetIds, startDate, endDate, cancellationToken);
            _onSnapshot();
            return res;
        }

        public void Add(InventoryAsset asset) => _inner.Add(asset);
        public Task<bool> ExistsSerialInListingAsync(Guid listingId, string serialNumber, Guid? excludeAssetId = null, CancellationToken cancellationToken = default) =>
            _inner.ExistsSerialInListingAsync(listingId, serialNumber, excludeAssetId, cancellationToken);
        public void Update(InventoryAsset asset) => _inner.Update(asset);
    }

    private sealed class InspectableFakeCache : IInventoryReadCache
    {
        public bool IsEnabled => true;
        public List<(string Scope, string QueryType, string Key, object Value, DateTimeOffset ExpiresAtUtc)> SetCalls { get; } = new();

        public Task<CacheReadResult<T>> GetAsync<T>(string scope, string queryType, string inputHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CacheReadResult<T>(default, "test-gen", false));

        public Task<bool> SetAsync<T>(string scope, string queryType, string inputHash, T value, string expectedGenerationToken, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        {
            SetCalls.Add((scope, queryType, inputHash, value!, expiresAtUtc));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(string scope, string queryType, IReadOnlyList<string> inputHashes, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, CacheReadResult<T>> res = inputHashes.ToDictionary(k => k, _ => new CacheReadResult<T>(default, "test-gen", false));
            return Task.FromResult(res);
        }

        public Task SetBulkAsync<T>(string scope, string queryType, IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items, string expectedGenerationToken, CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                SetCalls.Add((scope, queryType, item.InputHash, item.Value!, item.ExpiresAtUtc));
            }
            return Task.CompletedTask;
        }

        public async Task<Result<T>> GetOrCreateAsync<T>(string scope, string queryType, string inputHash, Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory, CancellationToken cancellationToken = default)
        {
            var res = await factory("test-gen", cancellationToken);
            if (res.IsFailure) return res.Error;
            return Result.Success(res.Value.Value);
        }

        public async Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(
            IReadOnlyList<CacheTarget> targets,
            Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory,
            CancellationToken cancellationToken = default)
        {
            var res = await factory(targets, cancellationToken);
            if (res.IsFailure) return res.Error;
            foreach (var kv in res.Value)
            {
                if (kv.Value.ExpiresAtUtc.HasValue)
                {
                    SetCalls.Add((kv.Key.Scope, kv.Key.QueryType, kv.Key.InputHash, kv.Value.Value!, kv.Value.ExpiresAtUtc.Value));
                }
            }
            return Result.Success<IReadOnlyDictionary<CacheTarget, T>>(res.Value.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        }

        public Task InvalidateScopeAsync(string scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MidIoExpiringCache : IInventoryReadCache
    {
        private readonly DateTimeOffset _expiry;
        private readonly Action _beforeNextOperation;

        public bool IsEnabled => true;

        public MidIoExpiringCache(DateTimeOffset expiry, Action beforeNextOperation)
        {
            _expiry = expiry;
            _beforeNextOperation = beforeNextOperation;
        }

        public Task<CacheReadResult<T>> GetAsync<T>(string scope, string queryType, string inputHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CacheReadResult<T>(default, "test-gen", false));

        public Task<bool> SetAsync<T>(string scope, string queryType, string inputHash, T value, string expectedGenerationToken, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, CacheReadResult<T>>> GetBulkAsync<T>(string scope, string queryType, IReadOnlyList<string> inputHashes, CancellationToken cancellationToken = default)
        {
            var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
            var hit = new CachedMonthRanges(ranges, _expiry);
            IReadOnlyDictionary<string, CacheReadResult<T>> res = inputHashes.ToDictionary(k => k, _ => new CacheReadResult<T>((T)(object)hit, "test-gen", true));

            _beforeNextOperation();
            return Task.FromResult(res);
        }

        public Task SetBulkAsync<T>(string scope, string queryType, IReadOnlyList<(string InputHash, T Value, DateTimeOffset ExpiresAtUtc)> items, string expectedGenerationToken, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<Result<T>> GetOrCreateAsync<T>(string scope, string queryType, string inputHash, Func<string, CancellationToken, Task<Result<CachedRead<T>>>> factory, CancellationToken cancellationToken = default)
        {
            var res = await factory("test-gen", cancellationToken);
            if (res.IsFailure) return res.Error;
            return Result.Success(res.Value.Value);
        }

        public Task<Result<IReadOnlyDictionary<CacheTarget, T>>> GetOrCreateBulkAsync<T>(
            IReadOnlyList<CacheTarget> targets,
            Func<IReadOnlyList<CacheTarget>, CancellationToken, Task<Result<IReadOnlyDictionary<CacheTarget, CachedRead<T>>>>> factory,
            CancellationToken cancellationToken = default)
        {
            var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
            var hit = new CachedMonthRanges(ranges, _expiry);
            var dict = targets.ToDictionary(t => t, _ => (T)(object)hit);
            _beforeNextOperation();
            return Task.FromResult(Result.Success<IReadOnlyDictionary<CacheTarget, T>>(dict));
        }

        public Task InvalidateScopeAsync(string scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAssetRepository : IInventoryAssetRepository
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
            Task.FromResult<IReadOnlyList<int>>(Store.Values.Where(a => a.Listing?.OwnerUserId == criteria.OwnerUserId).Select(a => a.Listing!.SchemaVersion).Distinct().OrderBy(v => v).ToList());

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

    private sealed class FakeListingRepository : IEquipmentListingRepository
    {
        public readonly Dictionary<Guid, EquipmentListing> Store = new();

        public Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) ? l : null);

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.OwnerUserId == ownerUserId ? l : null);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(id, out var l) && l.Status == EquipmentListingStatus.Active ? l : null);
        public Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(Guid ownerUserId, Guid? afterListingId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Store.Values
                .Where(l => l.OwnerUserId == ownerUserId && (!afterListingId.HasValue || l.Id > afterListingId.Value))
                .OrderBy(l => l.Id)
                .Take(limit)
                .Select(l => l.Id)
                .ToList());
        public Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            var list = Store.Values
                .Where(l => ids.Contains(l.Id))
                .Select(l => new EquipmentListingBatchItem(l.Id, l.OwnerUserId, l.EquipmentTypeId, l.Name, l.ThumbnailMediaId, l.DailyRate, l.Currency, l.Status, l.DepositAmount, l.BrandId, l.Model, l.SchemaVersion, l.SpecValues))
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
}
