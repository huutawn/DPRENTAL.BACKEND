using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task Patch_PhysicalDetails_Success200_AppliesPartialUpdatesWithPresenceTracking()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, serial: "SN-ORIG", year: 2022);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = """
        {
            "serialNumber": "SN-UPDATED",
            "manufactureYear": null
        }
        """;

        var content = new StringContent(patchJson, Encoding.UTF8, "application/json");
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(updated);
        Assert.Equal("SN-UPDATED", updated.SerialNumber);
        Assert.Null(updated.ManufactureYear);

        // Verify in PostgreSQL
        await using var db = CreateDbContext();
        var reloaded = await db.Assets.SingleAsync(a => a.Id == asset.Id);
        Assert.Equal("SN-UPDATED", reloaded.SerialNumber);
        Assert.Null(reloaded.ManufactureYear);
    }

    [Fact]
    public async Task ToggleAcceptingBookings_FalseTrue_PreservesAllocationsAndEmitsOutboxAtomically()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Booking Test Asset");

        // Seed an allocation and calendar block on the asset
        await using (var db = CreateDbContext())
        {
            var period = new Dprental.InventoryService.Domain.ValueObjects.RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
            var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), asset.Id, period, FixedNow).Value;
            db.AssetAllocations.Add(alloc);

            var block = CalendarBlock.Create(Guid.NewGuid(), asset.Id, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12),
                Dprental.InventoryService.Common.Enums.CalendarBlockReason.Maintenance, "op", FixedNow, new DateOnly(2026, 10, 1)).Value;
            db.CalendarBlocks.Add(block);

            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // 1. Disable bookings -> 200, emits Outbox event
        var disablePayload = new { acceptingBookings = false, reason = "Scheduled inspection" };
        var disableResp = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}/accepting-bookings",
            new StringContent(JsonSerializer.Serialize(disablePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);
        var disabled = await disableResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(disabled);
        Assert.False(disabled.AcceptingBookings);
        Assert.Equal("Scheduled inspection", disabled.SuspensionReason);

        // Verify Outbox message emitted atomically
        await using (var db = CreateDbContext())
        {
            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D") && m.Topic == "inventory.asset.events.v1").ToListAsync();
            Assert.Single(outboxMessages);
            Assert.Equal("inventory.asset.events.v1", outboxMessages[0].Topic);
        }

        // 2. Enable bookings -> 200, emits 2nd Outbox event
        var enablePayload = new { acceptingBookings = true };
        var enableResp = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}/accepting-bookings",
            new StringContent(JsonSerializer.Serialize(enablePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);
        var enabled = await enableResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(enabled);
        Assert.True(enabled.AcceptingBookings);
        Assert.Null(enabled.SuspensionReason);

        // Verify 2nd Outbox message emitted and allocations/blocks still intact
        await using (var db = CreateDbContext())
        {
            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D") && m.Topic == "inventory.asset.events.v1").ToListAsync();
            Assert.Equal(2, outboxMessages.Count);

            var allocs = await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync();
            Assert.Single(allocs);
            var blocks = await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync();
            Assert.Single(blocks);
        }
    }

    [Fact]
    public async Task OptimisticConcurrency_XminConflict_Returns409()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, serial: "Initial SN");

        int readCount = 0;
        var (app, secFactory) = CreateFactory(assetRepoFactory: sp =>
            new RaceInterceptingAssetRepository(
                new InventoryAssetRepository(sp.GetRequiredService<InventoryServiceDbContext>()),
                onAfterSnapshotRead: async (snap) =>
                {
                    if (Interlocked.Increment(ref readCount) == 1)
                    {
                        // Client 2 concurrently updates physical details directly in PostgreSQL
                        // between the snapshot read and the write transaction lock
                        await using var db2 = CreateDbContext();
                        var assetToUpdate = await db2.Assets.SingleAsync(a => a.Id == asset.Id);
                        assetToUpdate.UpdatePhysicalDetails("Concurrent SN", 2024, DateTimeOffset.UtcNow);
                        await db2.SaveChangesAsync();
                    }
                }));
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = """
        {
            "serialNumber": "Conflict SN"
        }
        """;

        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("STALE_INVENTORY_WRITE", body);

        // Verify in DB that asset was not corrupted or overwritten and prior update was retained
        await using (var db = CreateDbContext())
        {
            var currentSerial = (await db.Assets.SingleAsync(a => a.Id == asset.Id)).SerialNumber;
            Assert.Equal("Concurrent SN", currentSerial);
        }
    }

    [Fact]
    public async Task Post_Listing_BrandNotFound_Returns400ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            BrandHandler = _ => Task.FromResult(Result.Failure<CatalogBrandResponse>(
                Error.Validation("Brand.NotFound", "Thương hiệu không tồn tại trong Catalog.")))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-TEST-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Brand.NotFound", body);
        Assert.Equal(1, stubCatalog.LookupCalls);
        Assert.Equal(1, stubCatalog.BrandCalls);
        Assert.Equal(0, stubCatalog.SpecCalls);

        // Verify no listing written
        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == owner));
    }

    [Fact]
    public async Task Post_Listing_BrandInactive_Returns400ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            BrandHandler = id => Task.FromResult(Result.Success(
                new CatalogBrandResponse(id, "BR-OLD", "Old Brand", false)))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-TEST-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Brand.Inactive", body);
        Assert.Equal(1, stubCatalog.LookupCalls);
        Assert.Equal(1, stubCatalog.BrandCalls);
        Assert.Equal(0, stubCatalog.SpecCalls);

        // Verify no listing written
        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == owner));
    }

    [Fact]
    public async Task Post_Listing_SpecValidationFailure_CatalogReturns400_MapsTo400ProblemDetails_WithErrorsExtension()
    {
        var stubCatalog = new StubCatalogClient
        {
            SpecHandler = (typeId, ver, specs) =>
            {
                var catalogError = Error.Validation(
                    "SPEC_VALIDATION_FAILED",
                    "Thuộc tính không hợp lệ theo Catalog schema.");
                return Task.FromResult(Result.Failure(catalogError));
            }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-TEST-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("SPEC_VALIDATION_FAILED", problem.Title);
        Assert.Equal(1, stubCatalog.LookupCalls);
        Assert.Equal(1, stubCatalog.BrandCalls);
        Assert.Equal(1, stubCatalog.SpecCalls);

        // Database has NO record written
        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == owner));
    }

    [Fact]
    public async Task Patch_Listing_Brand_Success200_UpdatesBrandInDatabase()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var oldBrand = Guid.NewGuid();
        var newBrand = Guid.NewGuid();

        await using var initDb = CreateDbContext();
        var listing = await IntegrationTestHelper.InsertTestListingAsync(initDb, ownerId: owner, brandId: oldBrand);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = $"{{\"brandId\":\"{newBrand}\"}}";
        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(updated);
        Assert.Equal(newBrand, updated.BrandId);

        // Verify in PostgreSQL
        await using var db = CreateDbContext();
        var reloaded = await db.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal(newBrand, reloaded.BrandId);
    }
}
