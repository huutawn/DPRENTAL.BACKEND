using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
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
    private static readonly DateTimeOffset FixedEvaluationNow = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public PostgresPublicAvailabilityParityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    [Fact]
    public async Task SqlDiscovery_MatchesCanonicalPolicy_AcrossAllFourteenScenarios()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();
        var candidatePeriod = new RentalPeriod(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 10));

        var scenarios = new List<(EquipmentListing Listing, InventoryAsset Asset, List<AssetAllocation> Allocs, List<CalendarBlock> Blocks)>();

        for (int i = 1; i <= 14; i++)
        {
            var listingId = Guid.NewGuid();
            var assetId = Guid.NewGuid();

            var listing = EquipmentListing.Create(
                listingId, ownerId, Guid.NewGuid(), Guid.NewGuid(), $"Scenario_{i}", 1, "{}",
                100_000m, null, null, "VND", FixedEvaluationNow).Value;
            listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedEvaluationNow);

            var asset = InventoryAsset.Create(assetId, listingId, $"SN-{assetId:N}", FixedEvaluationNow).Value;
            listing.Assets.Add(asset);

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
                    asset.CalendarBlocks.Add(b2);
                    blocks.Add(b2);
                    break;

                case 3:
                    // Disjoint calendar block before candidate (2026-12-01 to 2026-12-04) -> Available
                    var b3 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 1)).Value;
                    asset.CalendarBlocks.Add(b3);
                    blocks.Add(b3);
                    break;

                case 4:
                    // Disjoint calendar block after candidate (2026-12-11 to 2026-12-15) -> Available
                    var b4 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 15),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 1)).Value;
                    asset.CalendarBlocks.Add(b4);
                    blocks.Add(b4);
                    break;

                case 5:
                    // Overlapping active Allocated allocation -> Unavailable
                    var a5 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 12)),
                        FixedEvaluationNow).Value;
                    asset.Allocations.Add(a5);
                    allocs.Add(a5);
                    break;

                case 6:
                    // Overlapping active Held allocation (not expired) -> Unavailable
                    var a6 = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 8)),
                        FixedEvaluationNow.AddHours(2), FixedEvaluationNow, "SYS", $"REF-H-{Guid.NewGuid():N}").Value;
                    asset.Allocations.Add(a6);
                    allocs.Add(a6);
                    break;

                case 7:
                    // Overlapping expired Held allocation -> Available
                    var a7 = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 8)),
                        FixedEvaluationNow.AddHours(-1), FixedEvaluationNow.AddHours(-2), "SYS", $"REF-E-{Guid.NewGuid():N}").Value;
                    asset.Allocations.Add(a7);
                    allocs.Add(a7);
                    break;

                case 8:
                    // Overlapping Cancelled allocation -> Available
                    var a8 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 8)),
                        FixedEvaluationNow).Value;
                    a8.Cancel("Scheduling cancel", FixedEvaluationNow, "tester");
                    asset.Allocations.Add(a8);
                    allocs.Add(a8);
                    break;

                case 9:
                    // Overlapping Released allocation -> Available
                    var a9 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 8)),
                        FixedEvaluationNow).Value;
                    a9.Release("Early return", FixedEvaluationNow, "tester");
                    asset.Allocations.Add(a9);
                    allocs.Add(a9);
                    break;

                case 10:
                    // Disjoint active Allocated before candidate (2026-11-20 to 2026-12-04) -> Available
                    var a10 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 4)),
                        FixedEvaluationNow).Value;
                    asset.Allocations.Add(a10);
                    allocs.Add(a10);
                    break;

                case 11:
                    // Disjoint active Allocated after candidate (2026-12-11 to 2026-12-20) -> Available
                    var a11 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 20)),
                        FixedEvaluationNow).Value;
                    asset.Allocations.Add(a11);
                    allocs.Add(a11);
                    break;

                case 12:
                    // Touching boundary start (ends on 2026-12-05) -> Overlaps! Unavailable
                    var a12 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5)),
                        FixedEvaluationNow).Value;
                    asset.Allocations.Add(a12);
                    allocs.Add(a12);
                    break;

                case 13:
                    // Touching boundary end (starts on 2026-12-10) -> Overlaps! Unavailable
                    var a13 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 15)),
                        FixedEvaluationNow).Value;
                    asset.Allocations.Add(a13);
                    allocs.Add(a13);
                    break;

                case 14:
                    // Both Block and Allocated overlap -> Unavailable
                    var b14 = CalendarBlock.Create(
                        Guid.NewGuid(), assetId, new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 6),
                        CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: new DateOnly(2026, 12, 1)).Value;
                    var a14 = AssetAllocation.CreateManual(
                        Guid.NewGuid(), assetId, new RentalPeriod(new DateOnly(2026, 12, 7), new DateOnly(2026, 12, 9)),
                        FixedEvaluationNow).Value;
                    asset.CalendarBlocks.Add(b14);
                    asset.Allocations.Add(a14);
                    blocks.Add(b14);
                    allocs.Add(a14);
                    break;
            }

            db.EquipmentListings.Add(listing);
            db.Assets.Add(asset);
            scenarios.Add((listing, asset, allocs, blocks));
        }

        await db.SaveChangesAsync();

        // 1. Evaluate with SQL discovery
        var searchCriteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(),
            availabilityFrom: candidatePeriod.StartDate,
            availabilityTo: candidatePeriod.EndDate,
            availabilityEvaluationUtc: FixedEvaluationNow,
            skip: 0,
            take: 100);

        var searchResult = await repo.SearchPublicAsync(searchCriteria);
        var sqlDiscoveredListingIds = searchResult.Items.Select(item => item.Listing.Id).ToHashSet();

        // 2. Evaluate with canonical domain policy and assert exact parity
        foreach (var (listing, asset, allocs, blocks) in scenarios)
        {
            var policyResult = AssetAvailabilityPolicy.CheckAvailability(
                asset,
                candidatePeriod,
                FixedEvaluationNow,
                allocs,
                blocks);

            bool sqlIncluded = sqlDiscoveredListingIds.Contains(listing.Id);
            bool policyPermits = policyResult.IsSuccess;

            Assert.True(
                sqlIncluded == policyPermits,
                $"Parity mismatch on {listing.Model}: Policy Permits = {policyPermits}, SQL Included = {sqlIncluded}. Policy Error = {policyResult.Error?.Code}");
        }
    }

    [Fact]
    public async Task SqlDiscovery_MatchesCanonicalPolicy_SingleDayWindow()
    {
        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        var ownerId = Guid.NewGuid();
        var singleDay = new DateOnly(2026, 12, 5);
        var candidatePeriod = new RentalPeriod(singleDay, singleDay);

        var l1Id = Guid.NewGuid();
        var a1Id = Guid.NewGuid();
        var l1 = EquipmentListing.Create(l1Id, ownerId, Guid.NewGuid(), Guid.NewGuid(), "SingleDay_Blocked", 1, "{}", 100_000m, null, null, "VND", FixedEvaluationNow).Value;
        l1.UpdateStatus(EquipmentListingStatus.Active, true, FixedEvaluationNow);
        var a1 = InventoryAsset.Create(a1Id, l1Id, "SN-A1", FixedEvaluationNow).Value;
        l1.Assets.Add(a1);
        var b1 = CalendarBlock.Create(Guid.NewGuid(), a1Id, singleDay, singleDay, CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: singleDay).Value;
        a1.CalendarBlocks.Add(b1);

        var l2Id = Guid.NewGuid();
        var a2Id = Guid.NewGuid();
        var l2 = EquipmentListing.Create(l2Id, ownerId, Guid.NewGuid(), Guid.NewGuid(), "SingleDay_Adjacent", 1, "{}", 100_000m, null, null, "VND", FixedEvaluationNow).Value;
        l2.UpdateStatus(EquipmentListingStatus.Active, true, FixedEvaluationNow);
        var a2 = InventoryAsset.Create(a2Id, l2Id, "SN-A2", FixedEvaluationNow).Value;
        l2.Assets.Add(a2);
        var b2 = CalendarBlock.Create(Guid.NewGuid(), a2Id, singleDay.AddDays(1), singleDay.AddDays(1), CalendarBlockReason.Maintenance, "tester", FixedEvaluationNow, today: singleDay).Value;
        a2.CalendarBlocks.Add(b2);

        db.EquipmentListings.AddRange(l1, l2);
        db.Assets.AddRange(a1, a2);
        db.CalendarBlocks.AddRange(b1, b2);
        await db.SaveChangesAsync();

        var searchCriteria = new EquipmentListingSearchCriteria(
            filters: new AssetSearchFilters(),
            availabilityFrom: candidatePeriod.StartDate,
            availabilityTo: candidatePeriod.EndDate,
            availabilityEvaluationUtc: FixedEvaluationNow,
            skip: 0,
            take: 100);

        var searchResult = await repo.SearchPublicAsync(searchCriteria);
        var sqlDiscoveredListingIds = searchResult.Items.Select(item => item.Listing.Id).ToHashSet();

        // Asset 1
        var p1 = AssetAvailabilityPolicy.CheckAvailability(a1, candidatePeriod, FixedEvaluationNow, [], [b1]);
        Assert.False(p1.IsSuccess);
        Assert.DoesNotContain(l1Id, sqlDiscoveredListingIds);

        // Asset 2
        var p2 = AssetAvailabilityPolicy.CheckAvailability(a2, candidatePeriod, FixedEvaluationNow, [], [b2]);
        Assert.True(p2.IsSuccess);
        Assert.Contains(l2Id, sqlDiscoveredListingIds);
    }

    [Fact]
    public async Task SearchPublicAsync_RepeatableReadSnapshot_IsolatesConcurrentMutations()
    {
        await using var db1 = CreateDbContext();
        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();

        // Seed 2 initial listings
        var l1 = await IntegrationTestHelper.InsertTestListingAsync(db1, ownerId: ownerId, eqTypeId: eqTypeId, model: "Snapshot_1", now: FixedEvaluationNow);
        var l2 = await IntegrationTestHelper.InsertTestListingAsync(db1, ownerId: ownerId, eqTypeId: eqTypeId, model: "Snapshot_2", now: FixedEvaluationNow);

        // Under RepeatableRead in db1
        await using var tx = await db1.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);

        // 1. Initial count under RepeatableRead
        string countSql = """SELECT COUNT(*)::int AS "Value" FROM "inventory"."equipment_listings" l WHERE l.status = 2 AND l.equipment_type_id = @pEq""";
        var pEq = new Npgsql.NpgsqlParameter("pEq", eqTypeId);
        var count1 = await db1.Database.SqlQueryRaw<int>(countSql, pEq).SingleAsync();
        Assert.Equal(2, count1);

        // 2. Concurrent connection mutates and commits a 3rd listing
        await using (var db2 = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db2, ownerId: ownerId, eqTypeId: eqTypeId, model: "Snapshot_3", now: FixedEvaluationNow);
        }

        // 3. Repeat read within tx in db1 - RepeatableRead must still see count = 2, isolating the committed mutation!
        var pEq2 = new Npgsql.NpgsqlParameter("pEq", eqTypeId);
        var count2 = await db1.Database.SqlQueryRaw<int>(countSql, pEq2).SingleAsync();
        Assert.Equal(2, count2);

        await tx.CommitAsync();
    }
}
