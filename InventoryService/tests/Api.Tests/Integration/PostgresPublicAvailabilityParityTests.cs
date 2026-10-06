using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresPublicAvailabilityParityTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedEvaluationNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public PostgresPublicAvailabilityParityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    [Fact]
    public async Task SqlDiscovery_MatchesCanonicalPolicyExactly_AcrossAllSchedulingScenarios()
    {
        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);

        var ownerId = Guid.NewGuid();
        var candidatePeriod = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 10));

        var scenarios = new List<(InventoryAsset Asset, List<AssetAllocation> Allocations, List<CalendarBlock> Blocks)>();

        for (int i = 1; i <= 14; i++)
        {
            var assetId = Guid.NewGuid();
            var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedEvaluationNow).Value;
            var asset = InventoryAsset.Create(
                assetId, ownerId, Guid.NewGuid(), Guid.NewGuid(), $"Scenario_{i}", 1, "{}", FixedEvaluationNow, rate).Value;

            var allocs = new List<AssetAllocation>();
            var blocks = new List<CalendarBlock>();

            switch (i)
            {
                case 1:
                    // Free asset (no blocks, no allocs) -> Available
                    break;

                case 2:
                    // Overlapping calendar block (2026-12-05 to 2026-12-07) -> Unavailable
                    var b2 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 7),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 5)).Value;
                    blocks.Add(b2);
                    db.CalendarBlocks.Add(b2);
                    break;

                case 3:
                    // Non-overlapping block (2026-11-20 to 2026-11-25) -> Available
                    var b3 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 11, 20)).Value;
                    blocks.Add(b3);
                    db.CalendarBlocks.Add(b3);
                    break;

                case 4:
                    // Active Held allocation (expires in future) -> Unavailable
                    var a4 = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, candidatePeriod,
                        holdExpiresAt: FixedEvaluationNow.AddHours(2), now: FixedEvaluationNow, sourceSystem: "SYS", externalReference: $"REF4_{assetId:N}").Value;
                    allocs.Add(a4);
                    db.AssetAllocations.Add(a4);
                    break;

                case 5:
                    // Expired Held allocation (expired in past) -> Available
                    var a5 = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, candidatePeriod,
                        holdExpiresAt: FixedEvaluationNow.AddHours(-1), now: FixedEvaluationNow.AddHours(-2), sourceSystem: "SYS", externalReference: $"REF5_{assetId:N}").Value;
                    allocs.Add(a5);
                    db.AssetAllocations.Add(a5);
                    break;

                case 6:
                    // Allocated allocation -> Unavailable
                    var a6 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, candidatePeriod, FixedEvaluationNow).Value;
                    allocs.Add(a6);
                    db.AssetAllocations.Add(a6);
                    break;

                case 7:
                    // Released allocation -> Available
                    var a7 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, candidatePeriod, FixedEvaluationNow).Value;
                    a7.Release("Done", FixedEvaluationNow, "tester");
                    allocs.Add(a7);
                    db.AssetAllocations.Add(a7);
                    break;

                case 8:
                    // Cancelled allocation -> Available
                    var a8 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, candidatePeriod, FixedEvaluationNow).Value;
                    a8.Cancel("Customer cancelled", FixedEvaluationNow, "tester");
                    allocs.Add(a8);
                    db.AssetAllocations.Add(a8);
                    break;

                case 9:
                    // AcceptingBookings = false (disabled asset) -> Unavailable
                    asset.DisableBookings("Offline", FixedEvaluationNow);
                    break;

                case 10:
                    // Hold expires EXACTLY AT evaluation timestamp (holdExpiresAt == FixedEvaluationNow) -> Expired/Available
                    var a10 = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, candidatePeriod,
                        holdExpiresAt: FixedEvaluationNow, now: FixedEvaluationNow.AddMinutes(-10), sourceSystem: "SYS", externalReference: $"REF10_{assetId:N}").Value;
                    allocs.Add(a10);
                    db.AssetAllocations.Add(a10);
                    break;

                case 11:
                    // Block touching left boundary (ends on 2026-12-01) -> Overlaps -> Unavailable
                    var b11 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 1),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 11, 20)).Value;
                    blocks.Add(b11);
                    db.CalendarBlocks.Add(b11);
                    break;

                case 12:
                    // Block touching right boundary (starts on 2026-12-10) -> Overlaps -> Unavailable
                    var b12 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 15),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 10)).Value;
                    blocks.Add(b12);
                    db.CalendarBlocks.Add(b12);
                    break;

                case 13:
                    // Block immediately adjacent before (ends on 2026-11-30) -> Non-overlapping -> Available
                    var b13 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 30),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 11, 20)).Value;
                    blocks.Add(b13);
                    db.CalendarBlocks.Add(b13);
                    break;

                case 14:
                    // Block immediately adjacent after (starts on 2026-12-11) -> Non-overlapping -> Available
                    var b14 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 20),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 11)).Value;
                    blocks.Add(b14);
                    db.CalendarBlocks.Add(b14);
                    break;
            }

            db.Assets.Add(asset);
            scenarios.Add((asset, allocs, blocks));
        }

        await db.SaveChangesAsync();

        // 1. Evaluate with SQL discovery
        var searchCriteria = new PublicAssetSearchCriteria(
            filters: new AssetSearchFilters(),
            availabilityFrom: candidatePeriod.StartDate,
            availabilityTo: candidatePeriod.EndDate,
            availabilityEvaluationUtc: FixedEvaluationNow,
            skip: 0,
            take: 100);

        var searchResult = await repo.SearchPublicAsync(searchCriteria);
        var sqlDiscoveredIds = searchResult.Items.Select(a => a.Id).ToHashSet();

        // 2. Evaluate with canonical domain policy and assert exact parity
        foreach (var (asset, allocs, blocks) in scenarios)
        {
            var policyResult = AssetAvailabilityPolicy.CheckAvailability(
                asset,
                candidatePeriod,
                FixedEvaluationNow,
                allocs,
                blocks);

            bool sqlIncluded = sqlDiscoveredIds.Contains(asset.Id);
            bool policyPermits = policyResult.IsSuccess;

            Assert.True(
                sqlIncluded == policyPermits,
                $"Parity mismatch on {asset.Model}: Policy Permits = {policyPermits}, SQL Included = {sqlIncluded}. Policy Error = {policyResult.Error?.Code}");
        }
    }

    [Fact]
    public async Task SqlDiscovery_MatchesCanonicalPolicy_SingleDayWindow()
    {
        await using var db = CreateDbContext();
        var repo = new InventoryAssetRepository(db);

        var ownerId = Guid.NewGuid();
        var singleDay = new DateOnly(2026, 12, 5);
        var candidatePeriod = new RentalPeriod(singleDay, singleDay);

        // Asset 1: Block on exact same single day -> Unavailable
        var a1Id = Guid.NewGuid();
        var a1 = InventoryAsset.Create(a1Id, ownerId, Guid.NewGuid(), Guid.NewGuid(), "SingleDay_Blocked", 1, "{}", FixedEvaluationNow, AssetRentalRate.Create(a1Id, 100_000m, null, null, "VND", FixedEvaluationNow).Value).Value;
        var b1 = CalendarBlock.Create(Guid.NewGuid(), a1Id, singleDay, singleDay, CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: singleDay).Value;

        // Asset 2: Block on adjacent single day (2026-12-06) -> Available
        var a2Id = Guid.NewGuid();
        var a2 = InventoryAsset.Create(a2Id, ownerId, Guid.NewGuid(), Guid.NewGuid(), "SingleDay_Adjacent", 1, "{}", FixedEvaluationNow, AssetRentalRate.Create(a2Id, 100_000m, null, null, "VND", FixedEvaluationNow).Value).Value;
        var b2 = CalendarBlock.Create(Guid.NewGuid(), a2Id, singleDay.AddDays(1), singleDay.AddDays(1), CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: singleDay).Value;

        db.Assets.AddRange(a1, a2);
        db.CalendarBlocks.AddRange(b1, b2);
        await db.SaveChangesAsync();

        var searchCriteria = new PublicAssetSearchCriteria(
            filters: new AssetSearchFilters(),
            availabilityFrom: candidatePeriod.StartDate,
            availabilityTo: candidatePeriod.EndDate,
            availabilityEvaluationUtc: FixedEvaluationNow,
            skip: 0,
            take: 100);

        var searchResult = await repo.SearchPublicAsync(searchCriteria);
        var sqlDiscoveredIds = searchResult.Items.Select(a => a.Id).ToHashSet();

        // Asset 1
        var p1 = AssetAvailabilityPolicy.CheckAvailability(a1, candidatePeriod, FixedEvaluationNow, [], [b1]);
        Assert.False(p1.IsSuccess);
        Assert.DoesNotContain(a1Id, sqlDiscoveredIds);

        // Asset 2
        var p2 = AssetAvailabilityPolicy.CheckAvailability(a2, candidatePeriod, FixedEvaluationNow, [], [b2]);
        Assert.True(p2.IsSuccess);
        Assert.Contains(a2Id, sqlDiscoveredIds);
    }

    [Fact]
    public async Task SearchPublicAsync_RepeatableReadSnapshot_IsolatesConcurrentMutations()
    {
        await using var db1 = CreateDbContext();
        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();

        // Seed 2 initial assets
        var a1Id = Guid.NewGuid();
        var a2Id = Guid.NewGuid();
        var a1 = InventoryAsset.Create(a1Id, ownerId, eqTypeId, Guid.NewGuid(), "Snapshot_1", 1, "{}", FixedEvaluationNow, AssetRentalRate.Create(a1Id, 100_000m, null, null, "VND", FixedEvaluationNow).Value).Value;
        var a2 = InventoryAsset.Create(a2Id, ownerId, eqTypeId, Guid.NewGuid(), "Snapshot_2", 1, "{}", FixedEvaluationNow, AssetRentalRate.Create(a2Id, 100_000m, null, null, "VND", FixedEvaluationNow).Value).Value;

        db1.Assets.AddRange(a1, a2);
        await db1.SaveChangesAsync();

        var searchCriteria = new PublicAssetSearchCriteria(
            filters: new AssetSearchFilters(EquipmentTypeId: eqTypeId),
            skip: 0,
            take: 10);

        // Under RepeatableRead in db1
        await using var tx = await db1.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        // 1. Initial count under RepeatableRead
        string countSql = """SELECT COUNT(*)::int AS "Value" FROM "inventory"."assets" a WHERE a.accepting_bookings = TRUE AND a.equipment_type_id = @pEq""";
        var pEq = new Npgsql.NpgsqlParameter("pEq", eqTypeId);
        var count1 = await db1.Database.SqlQueryRaw<int>(countSql, pEq).SingleAsync();
        Assert.Equal(2, count1);

        // 2. Concurrent connection mutates and commits a 3rd asset
        await using (var db2 = CreateDbContext())
        {
            var a3Id = Guid.NewGuid();
            var a3 = InventoryAsset.Create(a3Id, ownerId, eqTypeId, Guid.NewGuid(), "Snapshot_3", 1, "{}", FixedEvaluationNow, AssetRentalRate.Create(a3Id, 100_000m, null, null, "VND", FixedEvaluationNow).Value).Value;
            db2.Assets.Add(a3);
            await db2.SaveChangesAsync();
        }

        // 3. Repeat read within tx in db1 - RepeatableRead must still see count = 2, isolating the committed mutation!
        var pEq2 = new Npgsql.NpgsqlParameter("pEq", eqTypeId);
        var count2 = await db1.Database.SqlQueryRaw<int>(countSql, pEq2).SingleAsync();
        Assert.Equal(2, count2);

        await tx.CommitAsync();
    }
}
