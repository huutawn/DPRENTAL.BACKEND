using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class SharedCalendarIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public SharedCalendarIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(
                equipmentTypeId,
                1,
                [new CatalogBatchSchemaItemResponse(1, [])])));
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(TestTimeProvider? clock = null)
    {
        var testClock = clock ?? new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(new StubCatalogClient());

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new StubMediaClient());
            });
        });
        return (app, secFactory);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<EquipmentListing> CreateListingWithUnitsAsync(Guid ownerId, int unitCount, string serialPrefix = "FX3")
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3 Cinema Line",
            null,
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

    [Fact]
    public async Task GetListingCalendar_CountsOccupiedBlockedAvailable_AndEmptyDays_AndInclusiveBoundaries()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-CAL");

        List<InventoryAsset> units;
        await using (var db = CreateDbContext())
        {
            units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();
            Assert.Equal(3, units.Count);

            // Unit 1: Active hold on [2026-11-10, 2026-11-12]
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                FixedNow.AddDays(10),
                FixedNow,
                "partner",
                "EXT-HOLD-1").Value;
            db.AssetAllocations.Add(hold);

            // Unit 2: CalendarBlock on [2026-11-11, 2026-11-13]
            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                units[1].Id,
                new DateOnly(2026, 11, 11),
                new DateOnly(2026, 11, 13),
                CalendarBlockReason.Maintenance,
                "Regular maintenance",
                FixedNow,
                new DateOnly(2026, 10, 8)).Value;
            db.CalendarBlocks.Add(block);

            // Unit 3: Free
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var resp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-14");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal(listing.Id, calendar.ListingId);
        Assert.Equal(3, calendar.TotalQuantity);
        Assert.Equal(5, calendar.Days.Count); // 10, 11, 12, 13, 14 inclusive

        var day10 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 11, 10));
        Assert.Equal(1, day10.OccupiedQuantity);
        Assert.Equal(0, day10.BlockedQuantity);
        Assert.Equal(2, day10.AvailableQuantity);

        var day11 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 11, 11));
        Assert.Equal(1, day11.OccupiedQuantity);
        Assert.Equal(1, day11.BlockedQuantity);
        Assert.Equal(1, day11.AvailableQuantity);

        var day12 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 11, 12));
        Assert.Equal(1, day12.OccupiedQuantity);
        Assert.Equal(1, day12.BlockedQuantity);
        Assert.Equal(1, day12.AvailableQuantity);

        var day13 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 11, 13));
        Assert.Equal(0, day13.OccupiedQuantity);
        Assert.Equal(1, day13.BlockedQuantity);
        Assert.Equal(2, day13.AvailableQuantity);

        // Day 14: Empty day
        var day14 = calendar.Days.Single(d => d.Date == new DateOnly(2026, 11, 14));
        Assert.Equal(0, day14.OccupiedQuantity);
        Assert.Equal(0, day14.BlockedQuantity);
        Assert.Equal(3, day14.AvailableQuantity);
    }

    [Fact]
    public async Task GetListingCalendar_ExpiredHoldAndReleasedAllocation_DoNotCountAsOccupied()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-EXP");

        await using (var db = CreateDbContext())
        {
            var units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();

            // Unit 1: Expired hold (holdExpiresAt in the past relative to FixedNow)
            var expiredHold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                FixedNow.AddMinutes(-5),
                FixedNow.AddMinutes(-30),
                "partner",
                "EXT-HOLD-EXP").Value;
            db.AssetAllocations.Add(expiredHold);

            // Unit 2: Released manual allocation
            var manual = AssetAllocation.CreateManual(
                Guid.NewGuid(),
                units[1].Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                FixedNow.AddHours(-1),
                "EXT-MANUAL-REL").Value;
            manual.Release("Booking completed", FixedNow.AddMinutes(-10), "system");
            db.AssetAllocations.Add(manual);

            // Unit 3: Free
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var resp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal(3, calendar.TotalQuantity);

        foreach (var day in calendar.Days)
        {
            Assert.Equal(0, day.OccupiedQuantity);
            Assert.Equal(0, day.BlockedQuantity);
            Assert.Equal(3, day.AvailableQuantity);
        }
    }

    [Fact]
    public async Task GetListingCalendar_SuspendedUnit_CountsSuspendedOccupiedAndBlocked_ExcludedFromAvailable()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 3, "SN-SUSP");

        await using (var db = CreateDbContext())
        {
            var units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();

            // Unit 1: Suspended + Active Hold
            units[0].DisableBookings("Damaged", FixedNow);
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                FixedNow.AddDays(5),
                FixedNow,
                "partner",
                "EXT-SUSP-HOLD").Value;
            db.AssetAllocations.Add(hold);

            // Unit 2: Suspended + CalendarBlock
            units[1].DisableBookings("Under inspection", FixedNow);
            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                units[1].Id,
                new DateOnly(2026, 11, 10),
                new DateOnly(2026, 11, 12),
                CalendarBlockReason.Inspection,
                "Routine check",
                FixedNow,
                new DateOnly(2026, 10, 8)).Value;
            db.CalendarBlocks.Add(block);

            // Unit 3: Active and free
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var resp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);
        // Only 1 unit is accepting bookings
        Assert.Equal(1, calendar.TotalQuantity);

        foreach (var day in calendar.Days)
        {
            Assert.Equal(0, day.OccupiedQuantity);
            Assert.Equal(0, day.BlockedQuantity);
            Assert.Equal(1, day.AvailableQuantity);
            Assert.Equal(1, day.SuspendedOccupiedQuantity);
            Assert.Equal(1, day.SuspendedBlockedQuantity);
        }
    }

    [Fact]
    public async Task GetListingCalendar_DistinctUnitCounting_UnitWithBothHoldAndBlock_CountedOnceEach()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 2, "SN-BOTH");

        await using (var db = CreateDbContext())
        {
            var units = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).OrderBy(a => a.SerialNumber).ToListAsync();

            // Unit 1 has BOTH an active hold AND a calendar block on the same dates
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(),
                units[0].Id,
                RentalPeriod.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)).Value,
                FixedNow.AddDays(5),
                FixedNow,
                "partner",
                "EXT-HOLD-BOTH").Value;
            db.AssetAllocations.Add(hold);

            var block = CalendarBlock.Create(
                Guid.NewGuid(),
                units[0].Id,
                new DateOnly(2026, 11, 10),
                new DateOnly(2026, 11, 12),
                CalendarBlockReason.Maintenance,
                "Overlapping block",
                FixedNow,
                new DateOnly(2026, 10, 8)).Value;
            db.CalendarBlocks.Add(block);

            // Unit 2 is free
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var resp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var calendar = await resp.Content.ReadFromJsonAsync<ListingCalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal(2, calendar.TotalQuantity);

        foreach (var day in calendar.Days)
        {
            // Unit 1 counted as 1 occupied and 1 blocked; Unit 2 remains 1 available
            Assert.Equal(1, day.OccupiedQuantity);
            Assert.Equal(1, day.BlockedQuantity);
            Assert.Equal(1, day.AvailableQuantity);
        }
    }

    [Fact]
    public async Task GetListingCalendar_DateBoundaries_MaxWindowAndEndOfDomain_NoOverflow()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        var ownerId = Guid.NewGuid();
        var listing = await CreateListingWithUnitsAsync(ownerId, 1, "SN-OVF");

        using var scope = app.Services.CreateScope();
        var calendarService = scope.ServiceProvider.GetRequiredService<ICalendarService>();
        var caller = new TestCallerContext(ownerId, isHuman: true);

        // 1. Boundary: window ending at DateOnly.MaxValue (9999-12-31)
        var startMax = new DateOnly(9999, 12, 25);
        var endMax = DateOnly.MaxValue; // 9999-12-31
        var resultMax = await calendarService.GetListingCalendarAsync(listing.Id, startMax, endMax, caller);

        Assert.True(resultMax.IsSuccess, resultMax.Error.Message);
        Assert.Equal(7, resultMax.Value.Days.Count); // 25, 26, 27, 28, 29, 30, 31
        Assert.Equal(DateOnly.MaxValue, resultMax.Value.Days[^1].Date);

        // 2. Default window without from/to: automatically spans 6 months from today
        var defaultResult = await calendarService.GetListingCalendarAsync(listing.Id, null, null, caller);
        Assert.True(defaultResult.IsSuccess, defaultResult.Error.Message);
        Assert.Equal(listing.Id, defaultResult.Value.ListingId);
        Assert.True(defaultResult.Value.Days.Count >= 180);
    }

    [Fact]
    public async Task ListingCalendar_AuthorizationAndPrivacy_ContractEnforced()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        var listing = await CreateListingWithUnitsAsync(ownerId, 2, "SN-PRIV");
        InventoryAsset firstUnit;
        await using (var db = CreateDbContext())
        {
            firstUnit = await db.Assets.FirstAsync(a => a.EquipmentListingId == listing.Id);
        }

        // 1. Owner accesses Listing calendar -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var ownerResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, ownerResp.StatusCode);

        // 2. Admin accesses Listing calendar -> 200 OK (Listing calendar allows admin)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateAdminToken(adminId));
        var adminResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        // 3. Stranger accesses Listing calendar -> 403 Forbidden
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(strangerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var strangerResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.Forbidden, strangerResp.StatusCode);

        // 4. Physical Asset calendar: Owner -> 200 OK
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var assetOwnerResp = await client.GetAsync($"api/v1/inventory/assets/{firstUnit.Id:D}/calendar");
        Assert.Equal(HttpStatusCode.OK, assetOwnerResp.StatusCode);

        // 5. Physical Asset calendar: Admin -> 403 Forbidden (owner-only convention strictly maintained!)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateAdminToken(adminId));
        var assetAdminResp = await client.GetAsync($"api/v1/inventory/assets/{firstUnit.Id:D}/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, assetAdminResp.StatusCode);

        // 6. Privacy verification: Listing calendar JSON does not contain serial, assetCode, or unit IDs
        var calendarJson = await ownerResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SN-PRIV", calendarJson);
        Assert.DoesNotContain("AST-", calendarJson);
        Assert.DoesNotContain(firstUnit.Id.ToString("D"), calendarJson);

        // 7. Privacy verification: Public Listing discovery JSON does not leak physical IDs or serials
        var publicResp = await client.GetAsync($"api/v1/inventory/public/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.OK, publicResp.StatusCode);
        var publicJson = await publicResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SN-PRIV", publicJson);
        Assert.DoesNotContain("AST-", publicJson);
        Assert.DoesNotContain(firstUnit.Id.ToString("D"), publicJson);
    }

    private sealed class TestCallerContext : IInventoryCallerContext
    {
        public TestCallerContext(Guid userId, bool isHuman = true, bool isAdmin = false)
        {
            UserId = userId;
            IsHuman = isHuman;
            IsAdmin = isAdmin;
            IsAuthenticated = true;
        }

        public bool IsAuthenticated { get; }
        public bool IsHuman { get; }
        public bool IsService => !IsHuman;
        public bool IsAdmin { get; }
        public Guid? UserId { get; }
        public Guid? ClientId { get; set; }
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
        public Guid? OrganizationId { get; set; }
    }
}
