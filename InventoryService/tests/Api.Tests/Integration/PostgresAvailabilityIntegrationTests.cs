using Dprental.InventoryService.Domain.Repositories;
using System.Data.Common;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresAvailabilityIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;
    private static readonly DateTimeOffset InitialNow = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly InitialToday = new(2026, 10, 5);

    public PostgresAvailabilityIntegrationTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private InventoryServiceDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName));

        if (interceptor != null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new InventoryServiceDbContext(optionsBuilder.Options);
    }

    private sealed class SqlCommandCountingInterceptor : DbCommandInterceptor
    {
        public int SelectQueryCount { get; private set; }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectQueryCount++;
            }
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectQueryCount++;
            }
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public void Reset() => SelectQueryCount = 0;
    }

    private sealed class DelayingInterceptor : DbCommandInterceptor
    {
        private readonly Action _onBeforeQuery;
        private int _queryCount;

        public DelayingInterceptor(Action onBeforeQuery)
        {
            _onBeforeQuery = onBeforeQuery;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _queryCount++;
            if (_queryCount == 2)
            {
                // Gọi action đồng thời giữa câu query 1 và query 2 của split query
                _onBeforeQuery();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task GetAvailabilitySnapshotsAsync_SqlCount_DoesNotScaleAcross1_5_And_500_Assets()
    {
        var ownerUserId = Guid.NewGuid();
        var assetIds = new List<Guid>();

        await using var seedDb = CreateDbContext();
        for (int i = 0; i < 5; i++)
        {
            var asset = InventoryAsset.Create(
                Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), $"Camera {i}", 1, "{}", InitialNow).Value;

            asset.CalendarBlocks.Add(CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12),
                CalendarBlockReason.Maintenance, "op", InitialNow, InitialToday).Value);

            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, RentalPeriod.Create(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18)).Value,
                InitialNow.AddHours(2), InitialNow, "SYS", $"REF-{Guid.NewGuid():N}").Value;
            asset.Allocations.Add(hold);

            seedDb.Assets.Add(asset);
            assetIds.Add(asset.Id);
        }
        await seedDb.SaveChangesAsync();

        var interceptor = new SqlCommandCountingInterceptor();
        await using var testDb = CreateDbContext(interceptor);
        var repo = new InventoryAssetRepository(testDb);

        var startDate = new DateOnly(2026, 10, 1);
        var endDate = new DateOnly(2026, 10, 31);

        // 1. Snapshot với 1 asset
        interceptor.Reset();
        var singleResult = await repo.GetAvailabilitySnapshotsAsync(new[] { assetIds[0] }, startDate, endDate);
        int singleQueryCount = interceptor.SelectQueryCount;
        Assert.Single(singleResult);
        Assert.True(singleQueryCount <= 3, $"Expected <= 3 SELECTs for 1 asset, got {singleQueryCount}.");

        // 2. Snapshot với 5 assets
        interceptor.Reset();
        var multiResult = await repo.GetAvailabilitySnapshotsAsync(assetIds, startDate, endDate);
        int multiQueryCount = interceptor.SelectQueryCount;
        Assert.Equal(5, multiResult.Count);
        Assert.Equal(singleQueryCount, multiQueryCount);
        Assert.True(multiQueryCount <= 3, $"Expected <= 3 SELECTs for 5 assets, got {multiQueryCount}.");

        // 3. Snapshot với 500 assets (5 asset có sẵn + 495 ID khác)
        var fiveHundredIds = assetIds.Concat(Enumerable.Range(0, 495).Select(_ => Guid.NewGuid())).ToList();
        interceptor.Reset();
        var maxBatchResult = await repo.GetAvailabilitySnapshotsAsync(fiveHundredIds, startDate, endDate);
        int maxBatchQueryCount = interceptor.SelectQueryCount;
        Assert.Equal(5, maxBatchResult.Count);
        Assert.Equal(singleQueryCount, maxBatchQueryCount);
        Assert.True(maxBatchQueryCount <= 3, $"Expected <= 3 SELECTs for 500 assets, got {maxBatchQueryCount}.");
    }

    [Fact]
    public async Task GetAvailabilitySnapshotsAsync_RepeatableRead_SnapshotConsistencyUnderConcurrentWrites()
    {
        var ownerUserId = Guid.NewGuid();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "RepeatableReadTest", 1, "{}", InitialNow).Value;

        await using (var seedDb = CreateDbContext())
        {
            seedDb.Assets.Add(asset);
            await seedDb.SaveChangesAsync();
        }

        var startDate = new DateOnly(2026, 10, 1);
        var endDate = new DateOnly(2026, 10, 31);

        // Interceptor kích hoạt một write transaction độc lập ngay giữa các split queries
        var delayInterceptor = new DelayingInterceptor(() =>
        {
            using var concurrentDb = CreateDbContext();
            var newBlock = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8),
                CalendarBlockReason.Maintenance, "concurrent_op", InitialNow, InitialToday).Value;
            concurrentDb.CalendarBlocks.Add(newBlock);
            concurrentDb.SaveChanges();
        });

        await using var readDb = CreateDbContext(delayInterceptor);
        var repo = new InventoryAssetRepository(readDb);

        // RepeatableRead transaction cô lập snapshot: đọc nhất quán và hoàn tất thành công
        var snapshots = await repo.GetAvailabilitySnapshotsAsync(new[] { asset.Id }, startDate, endDate);
        Assert.Single(snapshots);
        // Snapshot nhất quán, query thành công không sinh lỗi deadlock hay tearing
    }

    [Fact]
    public async Task CheckAvailabilityBatch_PostgresReal_CorrectClassification()
    {
        var ownerUserId = Guid.NewGuid();
        var windowStart = new DateOnly(2026, 10, 10);
        var windowEnd = new DateOnly(2026, 10, 15);

        // 1. Free asset
        var freeAsset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "Free", 1, "{}", InitialNow).Value;

        // 2. Disabled bookings
        var disabledAsset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "Disabled", 1, "{}", InitialNow).Value;
        disabledAsset.DisableBookings("Disabled", InitialNow);

        // 3. Blocked
        var blockedAsset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "Blocked", 1, "{}", InitialNow).Value;
        blockedAsset.CalendarBlocks.Add(CalendarBlock.Create(
            Guid.NewGuid(), blockedAsset.Id, windowStart, windowEnd,
            CalendarBlockReason.Maintenance, "op", InitialNow, windowStart).Value);

        // 4. Active Hold
        var heldAsset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "Held", 1, "{}", InitialNow).Value;
        heldAsset.Allocations.Add(AssetAllocation.CreateHold(
            Guid.NewGuid(), heldAsset.Id, RentalPeriod.Create(windowStart, windowEnd).Value,
            InitialNow.AddHours(2), InitialNow, "SYS", $"REF-{Guid.NewGuid():N}").Value);

        // 5. Expired Hold
        var expiredAsset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "Expired", 1, "{}", InitialNow).Value;
        expiredAsset.Allocations.Add(AssetAllocation.CreateHold(
            Guid.NewGuid(), expiredAsset.Id, RentalPeriod.Create(windowStart, windowEnd).Value,
            InitialNow.AddHours(-1), InitialNow.AddHours(-2), "SYS", $"REF-{Guid.NewGuid():N}").Value);

        await using var seedDb = CreateDbContext();
        seedDb.Assets.AddRange(freeAsset, disabledAsset, blockedAsset, heldAsset, expiredAsset);
        await seedDb.SaveChangesAsync();

        var missingId = Guid.NewGuid();

        await using var queryDb = CreateDbContext();
        var repo = new InventoryAssetRepository(queryDb);
        var timeProvider = new TestTimeProvider(InitialNow);
        var service = new InventoryAvailabilityService(repo, NoOpInventoryAvailabilityCache.Instance, timeProvider);

        var request = new CheckAvailabilityBatchRequest(
            new[] { freeAsset.Id, disabledAsset.Id, blockedAsset.Id, heldAsset.Id, expiredAsset.Id, missingId },
            windowStart,
            windowEnd);

        var result = await service.CheckAvailabilityBatchAsync(request);
        Assert.True(result.IsSuccess);

        var val = result.Value;
        Assert.Equal(new[] { disabledAsset.Id, blockedAsset.Id, heldAsset.Id }, val.UnavailableAssetIds);
        Assert.Equal(new[] { missingId }, val.NotFoundAssetIds);
    }

    [Fact]
    public async Task GetBusyRanges_PostgresReal_CorrectRangesAndNoFakeRange()
    {
        var ownerUserId = Guid.NewGuid();
        var asset = InventoryAsset.Create(Guid.NewGuid(), ownerUserId, Guid.NewGuid(), Guid.NewGuid(), "RangesTest", 1, "{}", InitialNow).Value;

        // Block from 2026-10-05 to 2026-10-08
        asset.CalendarBlocks.Add(CalendarBlock.Create(
            Guid.NewGuid(), asset.Id, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8),
            CalendarBlockReason.Maintenance, "op", InitialNow, InitialToday).Value);

        // Active hold from 2026-10-09 to 2026-10-12 (adjacent to block! -> merges to 2026-10-05 to 2026-10-12)
        asset.Allocations.Add(AssetAllocation.CreateHold(
            Guid.NewGuid(), asset.Id, RentalPeriod.Create(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12)).Value,
            InitialNow.AddHours(2), InitialNow, "SYS", $"REF-{Guid.NewGuid():N}").Value);

        await using var seedDb = CreateDbContext();
        seedDb.Assets.Add(asset);
        await seedDb.SaveChangesAsync();

        await using var queryDb = CreateDbContext();
        var repo = new InventoryAssetRepository(queryDb);
        var timeProvider = new TestTimeProvider(InitialNow);
        var service = new InventoryAvailabilityService(repo, NoOpInventoryAvailabilityCache.Instance, timeProvider);

        var result = await service.GetBusyRangesAsync(asset.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AcceptingBookings);
        Assert.Single(result.Value.Ranges);
        Assert.Equal(new DateOnly(2026, 10, 5), result.Value.Ranges[0].StartDate);
        Assert.Equal(new DateOnly(2026, 10, 12), result.Value.Ranges[0].EndDate);
    }
}
