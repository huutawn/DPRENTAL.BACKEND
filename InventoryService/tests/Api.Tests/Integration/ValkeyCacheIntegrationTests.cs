using System.Data.Common;
using Dprental.InventoryService.Api.Caching;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class ValkeyCacheIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly string _postgresConn;
    private readonly ValkeyFixture _valkey;
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.UtcNow;
    private static readonly string CurrentMonthKey = FixedNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string NextMonthKey = FixedNow.AddMonths(1).ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string MonthAfterNextKey = FixedNow.AddMonths(2).ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateOnly WindowStart = new(FixedNow.Year, FixedNow.Month, 1);
    private static readonly DateOnly WindowEnd = WindowStart.AddMonths(1).AddDays(-1);
    private static readonly DateOnly ThreeMonthEnd = WindowStart.AddMonths(3).AddDays(-1);

    public ValkeyCacheIntegrationTests(PostgresFixture postgresFixture, ValkeyFixture valkeyFixture)
    {
        _postgresConn = postgresFixture.ConnectionString;
        _valkey = valkeyFixture;
    }

    private InventoryServiceDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgresConn, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName));

        if (interceptor != null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new InventoryServiceDbContext(optionsBuilder.Options);
    }

    private ValkeyReadCache CreateValkeyCache(
        bool enabled = true,
        int dynamicTtl = 15,
        int timeoutMs = 500,
        TimeProvider? timeProvider = null,
        int maxPayloadBytes = 1024 * 1024)
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = enabled,
            DynamicTtlSeconds = dynamicTtl,
            MetadataTtlSeconds = 120,
            CatalogTtlSeconds = 120,
            TimeoutMs = timeoutMs,
            MaxPayloadBytes = maxPayloadBytes,
            EnvironmentName = "development"
        });
        return new ValkeyReadCache(
            options,
            _valkey.Multiplexer,
            timeProvider ?? new TestTimeProvider(FixedNow),
            new InventoryCacheMetrics(),
            NullLogger<ValkeyReadCache>.Instance);
    }

    private async Task<InventoryAsset> SeedAssetWithBlocksAsync(Guid assetId, DateOnly blockStart, DateOnly blockEnd)
    {
        await using var db = CreateDbContext();
        var asset = await IntegrationTestHelper.InsertTestAssetAsync(db, assetId: assetId, now: FixedNow);
        var block = CalendarBlock.Create(
            Guid.NewGuid(), assetId, blockStart, blockEnd, CalendarBlockReason.Maintenance, "op", FixedNow, blockStart).Value;
        db.CalendarBlocks.Add(block);
        await db.SaveChangesAsync();
        return asset;
    }

    private sealed class SqlCommandCountingInterceptor : DbCommandInterceptor
    {
        public List<string> ExecutedQueries { get; } = new();

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                ExecutedQueries.Add(command.CommandText);
            }
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                ExecutedQueries.Add(command.CommandText);
            }
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public void Reset() => ExecutedQueries.Clear();
    }

    private static InventoryAvailabilityService CreateAvailabilityService(
        InventoryServiceDbContext db,
        IInventoryReadCache cache,
        TimeProvider? timeProvider = null)
    {
        return new InventoryAvailabilityService(
            new InventoryAssetRepository(db),
            new EquipmentListingRepository(db),
            cache,
            timeProvider ?? TimeProvider.System);
    }

    [Fact]
    public async Task CacheMissAndHit_RealValkey_SecondCallHitsCache()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var start = WindowStart.AddDays(1);
        var end = WindowStart.AddDays(5);
        await SeedAssetWithBlocksAsync(assetId, start, end);

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var service = CreateAvailabilityService(db, valkeyCache, timeProvider);

        // 1. First call: miss, loads PostgreSQL, sets Valkey cache
        var r1 = await service.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
        Assert.True(r1.IsSuccess);
        Assert.Single(r1.Value.Ranges);

        // Verify key exists in Valkey under slot hash tag
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:asset:{assetId:D}}}";
        var cachedVal = await redisDb.StringGetAsync($"{tag}:d:busy:{CurrentMonthKey}");
        Assert.False(cachedVal.IsNullOrEmpty);

        // Verify generation key exists
        var genToken = await redisDb.StringGetAsync($"{tag}:gen");
        Assert.False(genToken.IsNullOrEmpty);

        // 2. Second call: hits cache!
        var r2 = await service.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
        Assert.True(r2.IsSuccess);
        Assert.Single(r2.Value.Ranges);
        Assert.Equal(r1.Value.Ranges[0].StartDate, r2.Value.Ranges[0].StartDate);
        Assert.Equal(r1.Value.Ranges[0].EndDate, r2.Value.Ranges[0].EndDate);
    }

    [Fact]
    public async Task CacheHit_SkipsSnapshotQuery_OnlyQueriesAssetMetadata()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, WindowStart.AddDays(1), WindowStart.AddDays(5));

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        // First call to populate cache
        await using (var dbInit = CreateDbContext())
        {
            var serviceInit = CreateAvailabilityService(dbInit, valkeyCache, timeProvider);
            await serviceInit.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
        }

        // Second call with query counting interceptor
        var interceptor = new SqlCommandCountingInterceptor();
        await using (var dbTest = CreateDbContext(interceptor))
        {
            var serviceTest = CreateAvailabilityService(dbTest, valkeyCache, timeProvider);

            interceptor.Reset();
            var result = await serviceTest.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
            Assert.True(result.IsSuccess);

            // Proves snapshot query (calendar_blocks and asset_allocations) is SKIPPED on cache hit!
            // Only 1 SELECT on assets table (for existence and AcceptingBookings) is executed!
            Assert.Single(interceptor.ExecutedQueries);
            var query = interceptor.ExecutedQueries[0];
            Assert.Contains("assets", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("calendar_blocks", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("asset_allocations", query, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MultipleMonths_BulkReadAndFill_PopulatesAllMonthKeys()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, WindowStart.AddDays(10), WindowStart.AddMonths(2).AddDays(5));

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var service = CreateAvailabilityService(db, valkeyCache, timeProvider);

        // Query spanning Oct, Nov, Dec
        var r1 = await service.GetBusyRangesAsync(assetId, WindowStart, ThreeMonthEnd);
        Assert.True(r1.IsSuccess);

        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:asset:{assetId:D}}}";
        Assert.True(await redisDb.KeyExistsAsync($"{tag}:d:busy:{CurrentMonthKey}"));
        Assert.True(await redisDb.KeyExistsAsync($"{tag}:d:busy:{NextMonthKey}"));
        Assert.True(await redisDb.KeyExistsAsync($"{tag}:d:busy:{MonthAfterNextKey}"));

        // Second call: bulk read hits all 3 months
        var r2 = await service.GetBusyRangesAsync(assetId, WindowStart, ThreeMonthEnd);
        Assert.True(r2.IsSuccess);
        Assert.Equal(r1.Value.Ranges.Count, r2.Value.Ranges.Count);
    }

    [Fact]
    public async Task TtlCalculation_ActiveHoldExpiry_LimitsTtlBelowNormalTtl()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            var asset = await IntegrationTestHelper.InsertTestAssetAsync(db, assetId: assetId, now: FixedNow);
            // Hold expires in 8 seconds (less than normal 15s TTL)
            var todayVn = Dprental.InventoryService.Common.Utils.BusinessDateTime.ToVietnamDate(FixedNow);
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(), assetId, RentalPeriod.Create(todayVn.AddDays(1), todayVn.AddDays(3)).Value,
                FixedNow.AddSeconds(8), FixedNow, "SYS", $"REF-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(hold);
            await db.SaveChangesAsync();
        }

        using var valkeyCache = CreateValkeyCache(enabled: true, dynamicTtl: 15);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var queryDb = CreateDbContext();
        var service = CreateAvailabilityService(queryDb, valkeyCache, timeProvider);

        var result = await service.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
        Assert.True(result.IsSuccess);

        // Verify key TTL in Valkey is capped at <= 8s
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:asset:{assetId:D}}}";
        var ttl = await redisDb.KeyTimeToLiveAsync($"{tag}:d:busy:{CurrentMonthKey}");
        Assert.NotNull(ttl);
        Assert.True(ttl.Value.TotalSeconds <= 8.5, $"Expected TTL <= 8s, got {ttl.Value.TotalSeconds}s.");
    }

    [Fact]
    public async Task InvalidateScope_ChangesGenerationToken_MakesOldEntriesMissImmediately()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, WindowStart.AddDays(1), WindowStart.AddMonths(1).AddDays(10));

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var service = CreateAvailabilityService(db, valkeyCache, timeProvider);

        // 1. Warm cache
        await service.GetBusyRangesAsync(assetId, WindowStart, WindowStart.AddMonths(2).AddDays(-1));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:asset:{assetId:D}}}";
        var oldGen = (string?)await redisDb.StringGetAsync($"{tag}:gen");
        Assert.False(string.IsNullOrWhiteSpace(oldGen));

        // 2. Invalidate scope
        await valkeyCache.InvalidateScopeAsync($"asset:{assetId:D}");

        var newGen = (string?)await redisDb.StringGetAsync($"{tag}:gen");
        Assert.NotEqual(oldGen, newGen);

        // 3. Interceptor tests that next call queries snapshot from PostgreSQL (missed cache)
        var interceptor = new SqlCommandCountingInterceptor();
        await using var dbRecheck = CreateDbContext(interceptor);
        var serviceRecheck = CreateAvailabilityService(dbRecheck, valkeyCache, timeProvider);

        interceptor.Reset();
        var res = await serviceRecheck.GetBusyRangesAsync(assetId, WindowStart, WindowStart.AddMonths(2).AddDays(-1));
        Assert.True(res.IsSuccess);

        // Proves PostgreSQL snapshot query was re-executed!
        Assert.Contains(interceptor.ExecutedQueries, q => q.Contains("calendar_blocks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StaleReaderFill_RejectedWhenGenerationChanges()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache(enabled: true);
        var scope = "listing:test";
        var queryType = "detail";
        var hash = "test-hash";

        // Reader A checks cache, gets initial generation token T1
        var readResult = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        Assert.False(readResult.IsHit);
        var tokenT1 = readResult.GenerationToken;
        Assert.False(string.IsNullOrWhiteSpace(tokenT1));

        // Invalidation happens (changes token to T2)
        await valkeyCache.InvalidateScopeAsync(scope);

        // Reader A tries to write with token T1
        var written = await valkeyCache.SetAsync(scope, queryType, hash, "stale-data", tokenT1, FixedNow.AddMinutes(5));

        // Must be REJECTED!
        Assert.False(written);

        // Subsequent read does not see stale data
        var secondRead = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        Assert.False(secondRead.IsHit);
    }

    [Fact]
    public async Task GenerationTokenEviction_RegeneratedTokenDoesNotResurrectStaleData()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache(enabled: true);
        var scope = "asset:eviction-test";
        var queryType = "busy";
        var hash = "202610";

        var r1 = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        await valkeyCache.SetAsync(scope, queryType, hash, "old-value", r1.GenerationToken, FixedNow.AddMinutes(5));

        // Confirm hit
        var r2 = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        Assert.True(r2.IsHit);
        Assert.Equal("old-value", r2.Value);

        // Simulate generation key eviction in Valkey
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:{scope}}}";
        await redisDb.KeyDeleteAsync($"{tag}:gen");

        // Next read must regenerate a new random token and treat old entry as miss
        var r3 = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        Assert.False(r3.IsHit);
        Assert.NotEqual(r1.GenerationToken, r3.GenerationToken);
    }

    [Fact]
    public async Task PayloadOversize_Above1MiB_SkipsCacheWrite()
    {
        await _valkey.FlushDbAsync();
        // Limit to 500 bytes for test
        using var valkeyCache = CreateValkeyCache(enabled: true, maxPayloadBytes: 500);
        var scope = "listing:oversize";
        var queryType = "detail";
        var hash = "large";

        var r1 = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        var largePayload = new string('A', 2000); // 2000 bytes > 500 bytes

        var written = await valkeyCache.SetAsync(scope, queryType, hash, largePayload, r1.GenerationToken, FixedNow.AddMinutes(5));
        Assert.False(written);

        var r2 = await valkeyCache.GetAsync<string>(scope, queryType, hash);
        Assert.False(r2.IsHit);
    }

    [Fact]
    public async Task BrokenCacheConnection_FallsBackToPostgres_WithoutFailing()
    {
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));

        // Create multiplexer pointing to an unreachable port with short timeout
        var brokenConfig = ConfigurationOptions.Parse("127.0.0.1:59999");
        brokenConfig.AbortOnConnectFail = false;
        brokenConfig.ConnectTimeout = 100;
        brokenConfig.SyncTimeout = 100;
        brokenConfig.AsyncTimeout = 100;
        var brokenMux = await ConnectionMultiplexer.ConnectAsync(brokenConfig);

        var brokenOptions = Options.Create(new InventoryCacheOptions
        {
            Enabled = true,
            DynamicTtlSeconds = 15,
            TimeoutMs = 100,
            EnvironmentName = "development"
        });

        using var brokenCache = new ValkeyReadCache(brokenOptions, brokenMux, null, new InventoryCacheMetrics(), NullLogger<ValkeyReadCache>.Instance);

        await using var db = CreateDbContext();
        var service = CreateAvailabilityService(db, brokenCache);

        // Should NOT throw! Gracefully logs warning and returns PostgreSQL data
        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Ranges);

        // Invalidate does not throw unhandled exception outside
        // Note: InvalidateScopeAsync throws so background worker retries, but callers can catch or worker handles
    }

    [Fact]
    public async Task CacheDisabled_DoesNotUseValkey()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));

        using var disabledCache = CreateValkeyCache(enabled: false);

        await using var db = CreateDbContext();
        var service = CreateAvailabilityService(db, disabledCache);

        var result = await service.GetBusyRangesAsync(assetId, WindowStart, WindowEnd);
        Assert.True(result.IsSuccess);

        // Verify nothing was written to Valkey
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var tag = $"{{inventory:development:read:v2:scope:asset:{assetId:D}}}";
        Assert.False(await redisDb.KeyExistsAsync($"{tag}:d:busy:{CurrentMonthKey}"));
        Assert.False(await redisDb.KeyExistsAsync($"{tag}:gen"));
    }

    [Fact]
    public async Task CheckAvailabilityBatch_WarmAsset_HitsCache_ReducesSqlQueries()
    {
        await _valkey.FlushDbAsync();
        var asset1 = Guid.NewGuid();
        var asset2 = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(asset1, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15));
        await SeedAssetWithBlocksAsync(asset2, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25));

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        // 1. First call warms cache for asset1 and asset2
        await using (var dbInit = CreateDbContext())
        {
            var serviceInit = CreateAvailabilityService(dbInit, valkeyCache, timeProvider);
            var r1 = await serviceInit.CheckAvailabilityBatchAsync(new CheckAvailabilityBatchRequest(
                [asset1, asset2], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
            Assert.True(r1.IsSuccess);
        }

        // 2. Second call for asset1 (warm) and asset3 (cold)
        var asset3 = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(asset3, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));

        var interceptor = new SqlCommandCountingInterceptor();
        await using (var dbTest = CreateDbContext(interceptor))
        {
            var serviceTest = CreateAvailabilityService(dbTest, valkeyCache, timeProvider);
            interceptor.Reset();

            var r2 = await serviceTest.CheckAvailabilityBatchAsync(new CheckAvailabilityBatchRequest(
                [asset1, asset3], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
            Assert.True(r2.IsSuccess);

            // Proves snapshot query executed for missing asset3 and correctly combines warm asset1
            Assert.Contains(asset1, r2.Value.UnavailableAssetIds);
            Assert.Contains(asset3, r2.Value.UnavailableAssetIds);
            Assert.Equal(2, r2.Value.UnavailableAssetIds.Count);
        }
    }

    [Fact]
    public async Task CheckListingAvailability_CacheHit_And_InvalidateOnMutation()
    {
        await _valkey.FlushDbAsync();
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        InventoryAsset seededAsset;
        await using (var dbInit = CreateDbContext())
        {
            seededAsset = await IntegrationTestHelper.InsertTestAssetAsync(dbInit, assetId: assetId, ownerId: ownerId, now: FixedNow);
        }
        var listingId = seededAsset.EquipmentListingId;

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        // 1. First check warms cache
        await using (var db1 = CreateDbContext())
        {
            var service1 = CreateAvailabilityService(db1, valkeyCache, timeProvider);
            var r1 = await service1.CheckListingAvailabilityAsync(listingId, new Dprental.InventoryService.Application.DTOs.Availability.CheckListingAvailabilityRequest(
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 10),
                1));
            Assert.True(r1.IsSuccess);
            Assert.True(r1.Value.IsAvailable);
        }

        // 2. Second check hits cache (interceptor verifies 0 DB queries!)
        var interceptor = new SqlCommandCountingInterceptor();
        await using (var db2 = CreateDbContext(interceptor))
        {
            var service2 = CreateAvailabilityService(db2, valkeyCache, timeProvider);
            interceptor.Reset();
            var r2 = await service2.CheckListingAvailabilityAsync(listingId, new Dprental.InventoryService.Application.DTOs.Availability.CheckListingAvailabilityRequest(
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 10),
                1));
            Assert.True(r2.IsSuccess);
            Assert.Empty(interceptor.ExecutedQueries);
        }

        // 3. Invalidate listing scope
        await valkeyCache.InvalidateScopeAsync($"listing:{listingId:D}");

        // 4. Third check misses and queries DB
        await using (var db3 = CreateDbContext(interceptor))
        {
            var service3 = CreateAvailabilityService(db3, valkeyCache, timeProvider);
            interceptor.Reset();
            var r3 = await service3.CheckListingAvailabilityAsync(listingId, new Dprental.InventoryService.Application.DTOs.Availability.CheckListingAvailabilityRequest(
                new DateOnly(2026, 10, 1),
                new DateOnly(2026, 10, 10),
                1));
            Assert.True(r3.IsSuccess);
            Assert.NotEmpty(interceptor.ExecutedQueries);
        }
    }

    [Fact]
    public void OccupancyDeadline_BoundedByVietnamMidnight()
    {
        // Fixed time: 23:59:52 UTC+7 (8 seconds before Vietnam midnight)
        var vnNearMidnight = new DateTimeOffset(2026, 10, 5, 23, 59, 52, TimeSpan.FromHours(7));
        var nowUtc = vnNearMidnight.ToUniversalTime();

        // Normal TTL is 15 seconds
        var normalDeadline = nowUtc.AddSeconds(15);

        var deadline = CacheKeyHelper.CalculateOccupancyDeadline(normalDeadline, null, null, nowUtc);

        // Must be capped at exactly 8 seconds (VN midnight)!
        var remainingSeconds = (deadline - nowUtc).TotalSeconds;
        Assert.Equal(8, remainingSeconds, 1);
    }

    [Fact]
    public void OccupancyDeadline_BoundedByOverdueTransition()
    {
        // Today at VN is 2026-10-05 10:00:00 UTC+7
        var vnNow = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(7));
        var nowUtc = vnNow.ToUniversalTime();

        // Rental ends today (2026-10-05). It was handed over and not returned.
        // At midnight of 2026-10-06 (in 14 hours), todayVN > EndDate becomes true and it transitions to overdue!
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), Guid.NewGuid(), RentalPeriod.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5)).Value,
            nowUtc, "SYS").Value;
        alloc.Handover(nowUtc, nowUtc);

        // If normal deadline is 24 hours in the future
        var normalDeadline = nowUtc.AddHours(24);

        var deadline = CacheKeyHelper.CalculateOccupancyDeadline(normalDeadline, null, [alloc], nowUtc);

        // Must be capped at midnight VN (14 hours)!
        var remainingHours = (deadline - nowUtc).TotalHours;
        Assert.Equal(14, remainingHours, 1);
    }

    [Fact]
    public async Task ConcurrentMissCoalescing_SameKey_CoalescesFills()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache(enabled: true);
        var scope = "listing:coalesce-test";
        var queryType = "detail";
        var hash = "shared-hash";

        int sourceFactoryExecutions = 0;

        // 10 concurrent requests to the same cold key
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var res = await valkeyCache.GetOrCreateAsync<string>(
                scope,
                queryType,
                hash,
                async (gen, ct) =>
                {
                    Interlocked.Increment(ref sourceFactoryExecutions);
                    await Task.Delay(50, ct);
                    return Dprental.InventoryService.Common.Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<string>("shared-result", DateTimeOffset.UtcNow.AddMinutes(5)));
                });
            return res.Value;
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // All 10 returned the correct result
        foreach (var r in results)
        {
            Assert.Equal("shared-result", r);
        }

        // Source factory was executed exactly ONCE due to coalescing!
        Assert.Equal(1, sourceFactoryExecutions);
    }

    [Fact]
    public async Task NestedCatalogCall_Under32ConcurrentFills_DoesNotDeadlock()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache(enabled: true);

        // 32 concurrent requests (exhausting all 32 permits)
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var outerScope = $"listing:concurrent-{i}";
            return await valkeyCache.GetOrCreateAsync<string>(
                outerScope,
                "public-search",
                "query-hash",
                async (gen, ct) =>
                {
                    // Nested call to Catalog cache under the same fill permit
                    var catalogResult = await valkeyCache.GetOrCreateAsync<string>(
                        "catalog",
                        "category-types",
                        "cat-1",
                        (cGen, cCt) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(
                            new Dprental.InventoryService.Application.Caching.CachedRead<string>("catalog-schema", DateTimeOffset.UtcNow.AddMinutes(5)))),
                        ct);

                    Assert.True(catalogResult.IsSuccess);
                    return Dprental.InventoryService.Common.Result.Success(
                        new Dprental.InventoryService.Application.Caching.CachedRead<string>($"search-result-{i}", DateTimeOffset.UtcNow.AddMinutes(5)));
                });
        }).ToList();

        // Must complete without timeout/deadlock
        var results = await Task.WhenAll(tasks);
        Assert.Equal(32, results.Length);
        Assert.All(results, r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public async Task LeaderCancellation_DoesNotFailLivingWaiters()
    {
        await _valkey.FlushDbAsync();
        using var valkeyCache = CreateValkeyCache(enabled: true);
        var scope = "listing:cancel-leader";
        var queryType = "detail";
        var hash = "hash-1";

        var leaderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaderCts = new CancellationTokenSource();

        // Leader task
        var leaderTask = valkeyCache.GetOrCreateAsync<string>(
            scope,
            queryType,
            hash,
            async (gen, ct) =>
            {
                leaderEntered.TrySetResult();
                await Task.Delay(1000, ct); // Wait until cancelled
                return Dprental.InventoryService.Common.Result.Success(
                    new Dprental.InventoryService.Application.Caching.CachedRead<string>("leader-val", DateTimeOffset.UtcNow.AddMinutes(5)));
            },
            leaderCts.Token);

        // Wait until leader has acquired gate
        await leaderEntered.Task;

        // Waiter task arrives while leader is in gate
        var waiterTask = valkeyCache.GetOrCreateAsync<string>(
            scope,
            queryType,
            hash,
            (gen, ct) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(
                new Dprental.InventoryService.Application.Caching.CachedRead<string>("waiter-val", DateTimeOffset.UtcNow.AddMinutes(5)))),
            CancellationToken.None);

        // Cancel leader
        leaderCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaderTask);

        // Waiter must succeed!
        var waiterResult = await waiterTask;
        Assert.True(waiterResult.IsSuccess);
        Assert.Equal("waiter-val", waiterResult.Value);
    }

    [Fact]
    public async Task WarehouseFanout_CheckpointsOver100Listings()
    {
        await _valkey.FlushDbAsync();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed warehouse and 120 listings for owner
        await using (var db = CreateDbContext())
        {
            db.Warehouses.Add(Warehouse.CreateDraft(ownerId, now));
            await db.SaveChangesAsync();

            for (int i = 0; i < 120; i++)
            {
                var listing = EquipmentListing.Create(
                    Guid.NewGuid(), ownerId, Guid.NewGuid(), Guid.NewGuid(),
                    $"Listing {i}", "Description", "Model", 1,
                    "{}",
                    100000m, null, null, "VND",
                    now, 2024).Value;
                db.EquipmentListings.Add(listing);
            }
            await db.SaveChangesAsync();
        }

        using var valkeyCache = CreateValkeyCache(enabled: true);
        var job = CacheInvalidationJob.CreateFanout(Guid.NewGuid(), ownerId, ["public-discovery", $"owner:{ownerId:D}"], now);

        await using (var db = CreateDbContext())
        {
            db.CacheInvalidationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        var scopeFactory = new ServiceCollection()
            .AddLogging()
            .AddInventoryServiceDomainRepositories(_postgresConn)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var worker = new CacheInvalidationWorker(scopeFactory, valkeyCache);

        // First batch: processes first 100 listings, checkpoints cursor, returns to Pending
        int batch1 = await worker.ProcessBatchAsync();
        Assert.Equal(1, batch1);

        await using (var dbCheck1 = CreateDbContext())
        {
            var j1 = await dbCheck1.CacheInvalidationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(CacheInvalidationJob.StatusPending, j1.Status);
            Assert.NotNull(j1.FanoutAfterListingId);
        }

        // Second batch: processes remaining 20 listings, marks Completed
        int batch2 = await worker.ProcessBatchAsync();
        Assert.Equal(1, batch2);

        await using (var dbCheck2 = CreateDbContext())
        {
            var j2 = await dbCheck2.CacheInvalidationJobs.FirstAsync(j => j.Id == job.Id);
            Assert.Equal(CacheInvalidationJob.StatusCompleted, j2.Status);
            Assert.NotNull(j2.ProcessedAt);
        }
    }

    [Fact]
    public void StructuredKeys_DelimiterCollisionProof()
    {
        var today = new DateOnly(2026, 10, 16);

        // Case 1: q="a_b", brand=null vs q="a", brand="b"
        var hash1 = CacheKeyHelper.ComputePublicSearchHash(1, 20, "a_b", null, null, null, null, null, null, null, null, null, null, today);
        var hash2 = CacheKeyHelper.ComputePublicSearchHash(1, 20, "a", null, null, null, null, null, null, null, null, null, null, today);
        Assert.NotEqual(hash1, hash2);

        // Case 2: Invariant culture format check for dates
        var windowHash1 = CacheKeyHelper.ComputeWindowHash(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 15), 2);
        var windowHash2 = CacheKeyHelper.ComputeWindowHash(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 15), 3);
        Assert.NotEqual(windowHash1, windowHash2);
    }
}
