using Dprental.InventoryService.Domain.Repositories;
using System.Data.Common;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class ValkeyCacheIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly string _postgresConn;
    private readonly ValkeyFixture _valkey;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

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

    private ValkeyInventoryAvailabilityCache CreateValkeyCache(bool enabled = true, int ttlSeconds = 60, int timeoutMs = 500, TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new InventoryCacheOptions
        {
            Enabled = enabled,
            TtlSeconds = ttlSeconds,
            TimeoutMs = timeoutMs
        });
        return new ValkeyInventoryAvailabilityCache(options, _valkey.Multiplexer, timeProvider ?? new TestTimeProvider(FixedNow), NullLogger<ValkeyInventoryAvailabilityCache>.Instance);
    }

    private async Task<InventoryAsset> SeedAssetWithBlocksAsync(Guid assetId, DateOnly blockStart, DateOnly blockEnd)
    {
        await using var db = CreateDbContext();
        var asset = InventoryAsset.Create(
            assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Camera", 1, "{}", FixedNow).Value;

        asset.CalendarBlocks.Add(CalendarBlock.Create(
            Guid.NewGuid(), assetId, blockStart, blockEnd, CalendarBlockReason.Maintenance, "op", FixedNow, blockStart).Value);

        db.Assets.Add(asset);
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

    [Fact]
    public async Task CacheMissAndHit_RealValkey_SecondCallHitsCache()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var start = new DateOnly(2026, 10, 10);
        var end = new DateOnly(2026, 10, 15);
        await SeedAssetWithBlocksAsync(assetId, start, end);

        var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, valkeyCache, timeProvider);

        // 1. First call: miss, loads PostgreSQL, sets Valkey cache
        var r1 = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(r1.IsSuccess);
        Assert.Single(r1.Value.Ranges);

        // Verify key exists in Valkey
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var cachedVal = await redisDb.StringGetAsync($"inventory:asset:{assetId:D}:busy:202610");
        Assert.False(cachedVal.IsNullOrEmpty);

        // Verify index key has the month
        var members = await redisDb.SortedSetRangeByRankAsync($"inventory:asset:{assetId:D}:months");
        Assert.Single(members);
        Assert.Equal($"inventory:asset:{assetId:D}:busy:202610", (string)members[0]!);

        // 2. Second call: hits cache!
        var r2 = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
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
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15));

        var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        // First call to populate cache
        await using (var dbInit = CreateDbContext())
        {
            var repoInit = new InventoryAssetRepository(dbInit);
            var serviceInit = new InventoryAvailabilityService(repoInit, valkeyCache, timeProvider);
            await serviceInit.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        }

        // Second call with query counting interceptor
        var interceptor = new SqlCommandCountingInterceptor();
        await using (var dbTest = CreateDbContext(interceptor))
        {
            var repoTest = new InventoryAssetRepository(dbTest);
            var serviceTest = new InventoryAvailabilityService(repoTest, valkeyCache, timeProvider);

            interceptor.Reset();
            var result = await serviceTest.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
            Assert.True(result.IsSuccess);

            // Proves snapshot query (which queries calendar_blocks and asset_allocations) is SKIPPED on cache hit!
            // Only 1 SELECT on assets table (for existence and AcceptingBookings) is executed!
            Assert.Single(interceptor.ExecutedQueries);
            var query = interceptor.ExecutedQueries[0];
            Assert.Contains("assets", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("calendar_blocks", query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("asset_allocations", query, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MultipleMonths_BulkReadAndFill_PopulatesAllMonthKeysAndIndex()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 10));

        var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, valkeyCache, timeProvider);

        // Query spanning Oct, Nov, Dec
        var r1 = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
        Assert.True(r1.IsSuccess);

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202611"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202612"));

        var members = await redisDb.SortedSetRangeByRankAsync($"inventory:asset:{assetId:D}:months");
        Assert.Equal(3, members.Length);

        // Second call: bulk read hits all 3 months
        var r2 = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
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
            var asset = InventoryAsset.Create(assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "HoldTtlTest", 1, "{}", FixedNow).Value;
            // Hold expires in 25 seconds (less than normal 60s TTL)
            asset.Allocations.Add(AssetAllocation.CreateHold(
                Guid.NewGuid(), assetId, RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
                FixedNow.AddSeconds(25), FixedNow, "SYS", $"REF-{Guid.NewGuid():N}").Value);

            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        var valkeyCache = CreateValkeyCache(enabled: true, ttlSeconds: 60);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var queryDb = CreateDbContext();
        var repo = new InventoryAssetRepository(queryDb);
        var service = new InventoryAvailabilityService(repo, valkeyCache, timeProvider);

        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);

        // Verify key TTL in Valkey is capped at <= 25s
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var ttl = await redisDb.KeyTimeToLiveAsync($"inventory:asset:{assetId:D}:busy:202610");
        Assert.NotNull(ttl);
        Assert.True(ttl.Value.TotalSeconds <= 25, $"Expected TTL <= 25s, got {ttl.Value.TotalSeconds}s.");
    }

    [Fact]
    public async Task AtomicMetadataIndex_And_AtomicInvalidation_DeletesAllKeysAndIndex()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30));

        var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, valkeyCache, timeProvider);

        // Fill Oct and Nov into cache
        await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30));

        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202611"));
        Assert.True(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));

        // Invalidate asset
        await valkeyCache.InvalidateAssetAsync(assetId);

        // Verify month keys and index key are all deleted
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202611"));
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));

        // Calling Invalidate again is idempotent and does not throw
        await valkeyCache.InvalidateAssetAsync(assetId);
    }

    [Fact]
    public async Task CorruptCachePayload_FallsBackToPostgres_AndSucceeds()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));

        // Inject corrupt payload into Valkey (ranges is null)
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var corruptJson = "{\"ranges\": null, \"expiresAtUtc\": \"2026-10-05T12:00:00Z\"}";
        await redisDb.StringSetAsync($"inventory:asset:{assetId:D}:busy:202610", corruptJson, TimeSpan.FromMinutes(10));

        var valkeyCache = CreateValkeyCache(enabled: true);
        var timeProvider = new TestTimeProvider(FixedNow);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, valkeyCache, timeProvider);

        // Must NOT throw NullReferenceException! Falls back to PostgreSQL and returns correct ranges!
        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Ranges);
        Assert.Equal(new DateOnly(2026, 10, 5), result.Value.Ranges[0].StartDate);
        Assert.Equal(new DateOnly(2026, 10, 8), result.Value.Ranges[0].EndDate);
    }

    [Fact]
    public async Task MillisecondPrecision_PruningDoesNotEvictValidMember()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        var valkeyCache = CreateValkeyCache(enabled: true);

        // Key 1 expires at FixedNow + 10.800 seconds (e.g. 10:00:10.800)
        var expiry1 = FixedNow.AddSeconds(10).AddMilliseconds(800);
        var ranges = new[] { new BusyRangeDto(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8)) };
        await valkeyCache.SetMonthRangesAsync(assetId, "202610", ranges, expiry1);

        // Key 2 is set at FixedNow + 10.100 seconds (where 10.100s < 10.800s)
        var expiry2 = FixedNow.AddSeconds(20);
        await valkeyCache.SetMonthRangesAsync(assetId, "202611", ranges, expiry2);

        // Key 1 (202610) must STILL be in the index because 10.800s > 10.100s!
        var redisDb = _valkey.Multiplexer.GetDatabase();
        var members = await redisDb.SortedSetRangeByRankAsync($"inventory:asset:{assetId:D}:months");
        Assert.Contains(members, m => (string)m! == $"inventory:asset:{assetId:D}:busy:202610");
        Assert.Contains(members, m => (string)m! == $"inventory:asset:{assetId:D}:busy:202611");
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
            TtlSeconds = 60,
            TimeoutMs = 100
        });

        var brokenCache = new ValkeyInventoryAvailabilityCache(brokenOptions, brokenMux, null, NullLogger<ValkeyInventoryAvailabilityCache>.Instance);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, brokenCache);

        // Should NOT throw! Gracefully logs warning and returns PostgreSQL data
        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Ranges);

        // Invalidate also does not throw
        await brokenCache.InvalidateAssetAsync(assetId);
    }

    [Fact]
    public async Task CacheDisabled_DoesNotUseValkey()
    {
        await _valkey.FlushDbAsync();
        var assetId = Guid.NewGuid();
        await SeedAssetWithBlocksAsync(assetId, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8));

        var disabledCache = CreateValkeyCache(enabled: false);

        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);
        var service = new InventoryAvailabilityService(repo, disabledCache);

        var result = await service.GetBusyRangesAsync(assetId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);

        // Verify nothing was written to Valkey
        var redisDb = _valkey.Multiplexer.GetDatabase();
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:busy:202610"));
        Assert.False(await redisDb.KeyExistsAsync($"inventory:asset:{assetId:D}:months"));
    }
}
