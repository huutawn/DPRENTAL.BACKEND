using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class InventoryCalendarAndAssetFilterTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public InventoryCalendarAndAssetFilterTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventorySecurityTestFactory CreateFactory(TestTimeProvider? clock = null)
    {
        clock ??= new TestTimeProvider(FixedNow);
        return new InventorySecurityTestFactory(_fixture.ConnectionString, clock);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }
    private async Task<EquipmentListing> CreateListingWithUnitsAsync(Guid ownerId, int unitCount, string serialPrefix = "TEST")
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3 Cinema Line",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            FixedNow).Value;

        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        db.EquipmentListings.Add(listing);

        for (int i = 1; i <= unitCount; i++)
        {
            var unit = InventoryAsset.Create(
                Guid.NewGuid(),
                listing.Id,
                $"{serialPrefix}-{i:D4}",
                FixedNow).Value;
            db.Assets.Add(unit);
        }

        await db.SaveChangesAsync();
        return listing;
    }

    [Theory]
    [InlineData("2026-02", 28)]
    [InlineData("2024-02", 29)]
    [InlineData("2026-04", 30)]
    [InlineData("2026-10", 31)]
    [InlineData("9999-12", 31)]
    public void ValidateCalendarWindow_MonthDays_CalculatesExpectedDates(string month, int expectedDays)
    {
        var result = AvailabilityDateValidator.ValidateCalendarWindow(
            month,
            null,
            null,
            FixedToday,
            out var validStart,
            out var validEnd);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, validStart.Day);
        Assert.Equal(expectedDays, validEnd.Day);
        Assert.Equal(expectedDays, (validEnd.ToDateTime(TimeOnly.MinValue) - validStart.ToDateTime(TimeOnly.MinValue)).Days + 1);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("invalid")]
    [InlineData("2026-1")]
    [InlineData("2026-10-15")]
    [InlineData("0000-01")]
    [InlineData("٢٠٢٦-10")]
    public void ValidateCalendarWindow_InvalidMonthFormat_ReturnsValidationFailure(string invalidMonth)
    {
        var result = AvailabilityDateValidator.ValidateCalendarWindow(
            invalidMonth,
            null,
            null,
            FixedToday,
            out _,
            out _);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MONTH_FORMAT", result.Error.Code);
    }

    [Fact]
    public void ValidateCalendarWindow_ConflictingMonthAndFromTo_ReturnsConflictFailure()
    {
        var result = AvailabilityDateValidator.ValidateCalendarWindow(
            "2026-10",
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 10),
            FixedToday,
            out _,
            out _);

        Assert.True(result.IsFailure);
        Assert.Equal("CONFLICTING_DATE_PARAMETERS", result.Error.Code);
    }

    [Fact]
    public async Task GetListingCalendar_WithMonth_ReturnsDailyBreakdownAndTotalAssetQuantity()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 4, "SN-BRK");

        List<InventoryAsset> units;
        await using (var db = CreateDbContext())
        {
            units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();
            Assert.Equal(4, units.Count);

            // Unit 1: Allocated on 2026-10-10 to 2026-10-12
            var alloc = AssetAllocation.CreateManual(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
                FixedNow,
                operationalReference: $"EXT-MAN-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(alloc);

            // Unit 2: Held on 2026-10-10 to 2026-10-11
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[1].Id,
                RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11)).Value,
                FixedNow.AddHours(2),
                FixedNow,
                "partner",
                $"EXT-HLD-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(hold);

            // Unit 3: CalendarBlock Reason.Maintenance on 2026-10-11 to 2026-10-13, and suspended!
            units[2].DisableBookings("Routine maintenance", FixedNow);
            var maintBlock = CalendarBlock.Create(
                Guid.NewGuid(),
                units[2].Id,
                new DateOnly(2026, 10, 11),
                new DateOnly(2026, 10, 13),
                CalendarBlockReason.Maintenance,
                "Regular maint",
                FixedNow,
                FixedToday).Value;
            db.CalendarBlocks.Add(maintBlock);

            // Unit 4: CalendarBlock Reason.Inspection (Other) on 2026-10-12 to 2026-10-14
            var otherBlock = CalendarBlock.Create(
                Guid.NewGuid(),
                units[3].Id,
                new DateOnly(2026, 10, 12),
                new DateOnly(2026, 10, 14),
                CalendarBlockReason.Inspection,
                "Inspection",
                FixedNow,
                FixedToday).Value;
            db.CalendarBlocks.Add(otherBlock);

            await db.SaveChangesAsync();
        }

        var token = factory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/calendar?month=2026-10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal(listing.Id, calendar.ListingId);
        Assert.Equal(3, calendar.TotalQuantity); // 3 accepting bookings
        Assert.Equal(4, calendar.TotalAssetQuantity); // 4 total units
        Assert.Equal(31, calendar.Days.Count); // October has 31 days

        // Day 2026-10-10:
        // Unit 1 is Allocated -> rentedQuantity = 1
        // Unit 2 is Held -> heldQuantity = 1
        // Unit 3 is free on 10-10 (maint starts 10-11), but suspended -> not available
        // Unit 4 is free on 10-10, accepting bookings -> availableQuantity = 1
        var day10 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 10, 10));
        Assert.Equal(1, day10.RentedQuantity);
        Assert.Equal(1, day10.HeldQuantity);
        Assert.Equal(0, day10.MaintenanceQuantity);
        Assert.Equal(0, day10.OtherBlockedQuantity);
        Assert.Equal(1, day10.AvailableQuantity);

        // Day 2026-10-11:
        // Unit 1: Allocated -> rentedQuantity = 1
        // Unit 2: Held -> heldQuantity = 1
        // Unit 3: Maintenance -> maintenanceQuantity = 1 (suspended unit still counted in maintenance!)
        // Unit 4: Free -> availableQuantity = 1
        var day11 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 10, 11));
        Assert.Equal(1, day11.RentedQuantity);
        Assert.Equal(1, day11.HeldQuantity);
        Assert.Equal(1, day11.MaintenanceQuantity);
        Assert.Equal(0, day11.OtherBlockedQuantity);
        Assert.Equal(1, day11.AvailableQuantity);

        // Day 2026-10-12:
        // Unit 1: Allocated -> rentedQuantity = 1
        // Unit 2: Free -> available
        // Unit 3: Maintenance -> maintenanceQuantity = 1
        // Unit 4: Inspection (OtherBlocked) -> otherBlockedQuantity = 1
        var day12 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 10, 12));
        Assert.Equal(1, day12.RentedQuantity);
        Assert.Equal(0, day12.HeldQuantity);
        Assert.Equal(1, day12.MaintenanceQuantity);
        Assert.Equal(1, day12.OtherBlockedQuantity);
        Assert.Equal(1, day12.AvailableQuantity);
    }

    [Fact]
    public async Task GetListingCalendar_ConflictingMonthAndFromTo_Returns400()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 1);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/calendar?month=2026-10&from=2026-10-01&to=2026-10-10");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetAssetCalendar_WithMonth_ReturnsEntriesWithoutQuantity_AndOwnerOnly()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 1);

        InventoryAsset asset;
        await using (var db = CreateDbContext())
        {
            asset = await db.Assets.SingleAsync(a => a.EquipmentListingId == listing.Id);
            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                asset.Id,
                new DateOnly(2026, 10, 15),
                new DateOnly(2026, 10, 18),
                CalendarBlockReason.Maintenance,
                "Maint",
                FixedNow,
                FixedToday).Value;
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
        }

        // 1. Owner can access
        var ownerToken = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var resp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar?month=2026-10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<CalendarResponse>();
        Assert.NotNull(body);
        Assert.Equal(asset.Id, body.AssetId);
        Assert.Equal(new DateOnly(2026, 10, 1), body.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), body.EndDate);
        Assert.Single(body.Entries);
        Assert.Equal("CalendarBlock", body.Entries[0].Type);
        Assert.Equal("Maintenance", body.Entries[0].Reason);

        // 2. Admin cannot access (owner-only!)
        var adminToken = factory.CreateHumanToken(Guid.NewGuid(), roles: ["adm"]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var adminResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar?month=2026-10");
        Assert.Equal(HttpStatusCode.Forbidden, adminResp.StatusCode);
    }

    [Fact]
    public async Task GetListingAssets_WithFilter_FiltersAccuratelyBeforePagination()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 4, "SN-FLT");

        List<InventoryAsset> units;
        await using (var db = CreateDbContext())
        {
            units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();
            Assert.Equal(4, units.Count);

            // Unit 1: Rented on [2026-10-10, 2026-10-12]
            var alloc = AssetAllocation.CreateManual(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
                FixedNow,
                operationalReference: $"EXT-MAN-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(alloc);

            // Unit 2: Held on [2026-10-10, 2026-10-12]
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[1].Id,
                RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
                FixedNow.AddHours(2),
                FixedNow,
                "partner",
                $"EXT-HLD-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(hold);

            // Unit 3: Blocked on [2026-10-10, 2026-10-12] AND Unit 3 is paused (DisableBookings)
            units[2].DisableBookings("Paused", FixedNow);
            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                units[2].Id,
                new DateOnly(2026, 10, 10),
                new DateOnly(2026, 10, 12),
                CalendarBlockReason.Inspection,
                "Inspect",
                FixedNow,
                FixedToday).Value;
            db.CalendarBlocks.Add(block);

            // Unit 4: Completely free and accepting bookings
            await db.SaveChangesAsync();
        }

        var token = factory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Partial filter parameters -> 400
        var missingStatus = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12");
        Assert.Equal(HttpStatusCode.BadRequest, missingStatus.StatusCode);

        var missingDate = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?status=available");
        Assert.Equal(HttpStatusCode.BadRequest, missingDate.StatusCode);

        var invalidStatus = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);

        // 2. Filter status=available -> only Unit 4 matches
        var availResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=available&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, availResp.StatusCode);
        var availPaged = await availResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(availPaged);
        Assert.Equal(1, availPaged.TotalCount);
        Assert.Single(availPaged.Items);
        Assert.Equal(units[3].Id, availPaged.Items[0].Id);

        // 3. Filter status=rented -> Unit 1 matches
        var rentedResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=rented&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, rentedResp.StatusCode);
        var rentedPaged = await rentedResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(rentedPaged);
        Assert.Equal(1, rentedPaged.TotalCount);
        Assert.Equal(units[0].Id, rentedPaged.Items[0].Id);

        // 4. Filter status=held -> Unit 2 matches
        var heldResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=held&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, heldResp.StatusCode);
        var heldPaged = await heldResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(heldPaged);
        Assert.Equal(1, heldPaged.TotalCount);
        Assert.Equal(units[1].Id, heldPaged.Items[0].Id);

        // 5. Filter status=blocked -> Unit 3 matches (even though paused!)
        var blockedResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=blocked&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, blockedResp.StatusCode);
        var blockedPaged = await blockedResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(blockedPaged);
        Assert.Equal(1, blockedPaged.TotalCount);
        Assert.Equal(units[2].Id, blockedPaged.Items[0].Id);
    }
    [Fact]
    public async Task GetListingAssets_FilterWithPagination_PagesCorrectlyUnderPostgres()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-PAGE");

        var token = factory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Page 1, pageSize 2
        var p1Resp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=available&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, p1Resp.StatusCode);
        var p1 = await p1Resp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(p1);
        Assert.Equal(3, p1.TotalCount);
        Assert.Equal(2, p1.TotalPages);
        Assert.Equal(2, p1.Items.Count);

        // Page 2, pageSize 2
        var p2Resp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-10&to=2026-10-12&status=available&page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, p2Resp.StatusCode);
        var p2 = await p2Resp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(p2);
        Assert.Equal(3, p2.TotalCount);
        Assert.Single(p2.Items);

        // Ensure distinct items across pages
        var page1Ids = p1.Items.Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain(p2.Items[0].Id, page1Ids);
    }

    [Fact]
    public async Task GetListingAssets_UnitWithRentalAndBlockOnDifferentDates_MatchesBothFilters()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 1, "SN-BOTH");

        InventoryAsset unit;
        await using (var db = CreateDbContext())
        {
            unit = await db.Assets.SingleAsync(a => a.EquipmentListingId == listing.Id);

            // Rental on [2026-10-10, 2026-10-12]
            var alloc = AssetAllocation.CreateManual(
                Guid.NewGuid(),
                unit.Id,
                RentalPeriod.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12)).Value,
                FixedNow,
                operationalReference: $"EXT-MAN-{Guid.NewGuid():N}").Value;
            db.AssetAllocations.Add(alloc);

            // Block on [2026-10-15, 2026-10-18]
            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                unit.Id,
                new DateOnly(2026, 10, 15),
                new DateOnly(2026, 10, 18),
                CalendarBlockReason.Maintenance,
                "Maint",
                FixedNow,
                FixedToday).Value;
            db.CalendarBlocks.Add(block);

            await db.SaveChangesAsync();
        }

        var token = factory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Overlap with [2026-10-09, 2026-10-20] which covers both:
        var rentedResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-09&to=2026-10-20&status=rented");
        Assert.Equal(HttpStatusCode.OK, rentedResp.StatusCode);
        var rentedPaged = await rentedResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(rentedPaged);
        Assert.Equal(1, rentedPaged.TotalCount);

        var blockedResp = await client.GetAsync($"/api/v1/inventory/equipment-listings/{listing.Id}/assets?from=2026-10-09&to=2026-10-20&status=blocked");
        Assert.Equal(HttpStatusCode.OK, blockedResp.StatusCode);
        var blockedPaged = await blockedResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(blockedPaged);
        Assert.Equal(1, blockedPaged.TotalCount);
    }
}
