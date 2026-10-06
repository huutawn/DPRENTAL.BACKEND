using System.Net;
using System.Net.Http.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task GetPublicAssets_NoDatesSpecified_ReturnsAssetsEvenIfCalendarOccupied()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var uniqueModel = "BusyAsset_" + Guid.NewGuid().ToString("N");
        var asset = await SeedAssetAsync(owner, model: uniqueModel);

        // Add calendar block on asset
        await using (var db = CreateDbContext())
        {
            var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
            var end = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
            var block = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, start, end,
                CalendarBlockReason.Maintenance, "tester", FixedNow, today: start).Value;
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
        }

        // Query without date parameters
        var response = await client.GetAsync($"api/v1/inventory/public/assets?q={uniqueModel}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(asset.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetPublicAssets_CalendarBlockOverlap_ExcludesAsset()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();
        var owner = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var modelBlocked = "BlockedAsset_" + Guid.NewGuid().ToString("N");
        var modelFree = "FreeAsset_" + Guid.NewGuid().ToString("N");

        var assetBlocked = await SeedAssetAsync(owner, model: modelBlocked, eqTypeId: eqTypeId);
        var assetFree = await SeedAssetAsync(owner, model: modelFree, eqTypeId: eqTypeId);

        var windowStart = new DateOnly(2026, 11, 1);
        var windowEnd = new DateOnly(2026, 11, 10);

        // Calendar block overlaps with window: 2026-11-05 to 2026-11-07
        await using (var db = CreateDbContext())
        {
            var block = CalendarBlock.Create(
                Guid.NewGuid(), assetBlocked.Id,
                new DateOnly(2026, 11, 5),
                new DateOnly(2026, 11, 7),
                CalendarBlockReason.Maintenance, "tester", FixedNow, today: new DateOnly(2026, 11, 5)).Value;
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&from=2026-11-01&to=2026-11-10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);

        var ids = paged.Items.Select(i => i.Id).ToList();
        Assert.Contains(assetFree.Id, ids);
        Assert.DoesNotContain(assetBlocked.Id, ids);
    }

    [Fact]
    public async Task GetPublicAssets_Allocations_HeldVsAllocatedVsReleased()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var modelActiveHeld = "ActiveHeld_" + Guid.NewGuid().ToString("N");
        var modelExpiredHeld = "ExpiredHeld_" + Guid.NewGuid().ToString("N");
        var modelAllocated = "Allocated_" + Guid.NewGuid().ToString("N");
        var modelReleased = "Released_" + Guid.NewGuid().ToString("N");

        var assetActiveHeld = await SeedAssetAsync(owner, model: modelActiveHeld, eqTypeId: eqTypeId);
        var assetExpiredHeld = await SeedAssetAsync(owner, model: modelExpiredHeld, eqTypeId: eqTypeId);
        var assetAllocated = await SeedAssetAsync(owner, model: modelAllocated, eqTypeId: eqTypeId);
        var assetReleased = await SeedAssetAsync(owner, model: modelReleased, eqTypeId: eqTypeId);
        var period = new RentalPeriod(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 10));

        await using (var db = CreateDbContext())
        {
            // 1. Active Held: hold_expires_at is future
            var activeHeld = AssetAllocation.CreateHold(
                Guid.NewGuid(), assetActiveHeld.Id, period,
                holdExpiresAt: FixedNow.AddHours(2), now: FixedNow, sourceSystem: "SYS", externalReference: "REF1").Value;

            // 2. Expired Held: hold_expires_at is in the past relative to FixedNow
            var expiredHeld = AssetAllocation.CreateHold(
                Guid.NewGuid(), assetExpiredHeld.Id, period,
                holdExpiresAt: FixedNow.AddHours(-1), now: FixedNow.AddHours(-2), sourceSystem: "SYS", externalReference: "REF2").Value;

            // 3. Allocated: occupies calendar
            var allocated = AssetAllocation.CreateManual(
                Guid.NewGuid(), assetAllocated.Id, period, FixedNow).Value;

            // 4. Released: does NOT occupy calendar
            var released = AssetAllocation.CreateManual(
                Guid.NewGuid(), assetReleased.Id, period, FixedNow).Value;
            released.Release("Done", FixedNow, "tester");
            db.AssetAllocations.AddRange(activeHeld, expiredHeld, allocated, released);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&from=2026-11-01&to=2026-11-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);

        var ids = paged.Items.Select(i => i.Id).ToList();
        // ActiveHeld and Allocated excluded!
        Assert.DoesNotContain(assetActiveHeld.Id, ids);
        Assert.DoesNotContain(assetAllocated.Id, ids);

        // ExpiredHeld and Released included!
        Assert.Contains(assetExpiredHeld.Id, ids);
        Assert.Contains(assetReleased.Id, ids);
    }

    [Fact]
    public async Task GetPublicAssets_UnavailableAheadInSortOrder_PageRemainsFull()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var uniquePrefix = "PagingTest_" + Guid.NewGuid().ToString("N");

        // Seed 5 assets, created in order: A1, A2, A3, A4, A5
        // Default sort is created_desc, so order is A5, A4, A3, A2, A1
        // We block A5 and A4 (the newest ones that would appear first).
        // If pageSize=2 is requested, the page should return A3 and A2 (full page of 2 available assets)!
        var a1 = await SeedAssetAsync(owner, model: $"{uniquePrefix}_A1", createdAt: FixedNow.AddMinutes(1));
        var a2 = await SeedAssetAsync(owner, model: $"{uniquePrefix}_A2", createdAt: FixedNow.AddMinutes(2));
        var a3 = await SeedAssetAsync(owner, model: $"{uniquePrefix}_A3", createdAt: FixedNow.AddMinutes(3));
        var a4 = await SeedAssetAsync(owner, model: $"{uniquePrefix}_A4", createdAt: FixedNow.AddMinutes(4));
        var a5 = await SeedAssetAsync(owner, model: $"{uniquePrefix}_A5", createdAt: FixedNow.AddMinutes(5));

        var windowFrom = new DateOnly(2026, 12, 1);
        var windowTo = new DateOnly(2026, 12, 5);

        await using (var db = CreateDbContext())
        {
            var b4 = CalendarBlock.Create(Guid.NewGuid(), a4.Id, windowFrom, windowTo, CalendarBlockReason.Maintenance, "tester", FixedNow, today: windowFrom).Value;
            var b5 = CalendarBlock.Create(Guid.NewGuid(), a5.Id, windowFrom, windowTo, CalendarBlockReason.Maintenance, "tester", FixedNow, today: windowFrom).Value;
            db.CalendarBlocks.AddRange(b4, b5);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"api/v1/inventory/public/assets?q={uniquePrefix}&from=2026-12-01&to=2026-12-05&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);

        // Must be a full page of 2 items
        Assert.Equal(2, paged.Items.Count);
        // Total available count should be 3 (A1, A2, A3)
        Assert.Equal(3, paged.TotalCount);
        Assert.Equal(2, paged.TotalPages);

        var returnedIds = paged.Items.Select(i => i.Id).ToList();
        Assert.Equal([a3.Id, a2.Id], returnedIds);
    }

    [Fact]
    public async Task GetPublicAssets_HoldExpiringDuringCatalogLookup_IncludedInResults()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient();
        var clock = new TestTimeProvider(FixedNow);

        // When Catalog is called, clock advances by 10 minutes!
        stubCatalog.BatchSchemasHandler = (id, versions) =>
        {
            clock.UtcNow = FixedNow.AddMinutes(10);
            return Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("power", "Power", "number", true, true, false, 0, 0, 1000, null, "kW", null)
                    ])
                ])));
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog, clock: clock);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "MidLookupExpiredAsset", eqTypeId: eqTypeId, specValues: "{\"power\": 50}");

        var period = new RentalPeriod(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 10));

        // Hold expires at FixedNow + 5 minutes
        await using (var db = CreateDbContext())
        {
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, period,
                holdExpiresAt: FixedNow.AddMinutes(5), now: FixedNow, sourceSystem: "SYS", externalReference: "REF-MID").Value;
            db.AssetAllocations.Add(hold);
            await db.SaveChangesAsync();
        }

        var filterJson = Uri.EscapeDataString("[{\"field\":\"power\",\"operator\":\"gte\",\"value\":40}]");
        var response = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}&from=2026-11-05&to=2026-11-10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);

        // Because evaluation timestamp was taken AFTER Catalog (at FixedNow + 10m), the hold (expired at +5m) is EXPIRED!
        // Asset is therefore available and included in the result!
        Assert.Single(paged.Items);
        Assert.Equal(asset.Id, paged.Items[0].Id);
    }
}
