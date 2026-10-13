using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task CreateListing_WithValidRentalRate_PersistsListingWithRates()
    {
        var owner = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3 Cinema Line",
            model = "Sony FX3 Cinema Line",
            serialNumber = "FX3-TEST-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { resolution = "4K" },
            rentalRate = new
            {
                dailyRate = 500_000m,
                weeklyRate = 3_000_000m,
                monthlyRate = 10_000_000m,
                currency = "VND"
            }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(created);
        Assert.NotNull(created.RentalRate);
        Assert.Equal(500_000m, created.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, created.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, created.RentalRate.MonthlyRate);
        Assert.Equal("VND", created.RentalRate.Currency);

        // Verify persisted in PostgreSQL
        await using var db = CreateDbContext();
        var listingDb = await db.EquipmentListings.FirstOrDefaultAsync(l => l.Id == created.Id);
        Assert.NotNull(listingDb);
        Assert.Equal(500_000m, listingDb.DailyRate);
        Assert.Equal(3_000_000m, listingDb.WeeklyRate);
        Assert.Equal(10_000_000m, listingDb.MonthlyRate);
        Assert.Equal("VND", listingDb.Currency);
    }

    [Fact]
    public async Task CreateListing_MissingRentalRate_FailsBeforeCallingCatalogOrMedia()
    {
        var owner = Guid.NewGuid();
        int catalogCalls = 0;

        var stubCatalog = new StubCatalogClient
        {
            LookupHandler = _ =>
            {
                catalogCalls++;
                return Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                    Guid.NewGuid(), "EQ-FX3", "Sony FX3", null, 1, DateTime.UtcNow, null, null)));
            }
        };

        var stubMedia = new StubMediaClient();

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog, mediaClient: stubMedia);
        using var client = app.CreateClient();
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
            specValues = new { resolution = "4K" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, catalogCalls);
        Assert.Equal(0, stubMedia.VerifyCalls);
    }

    [Theory]
    [InlineData("{\"dailyRate\": 500000.5, \"currency\": \"VND\"}")] // Decimal / fraction
    [InlineData("{\"dailyRate\": -100000, \"currency\": \"VND\"}")] // Negative
    [InlineData("{\"dailyRate\": 0, \"currency\": \"VND\"}")] // Zero
    [InlineData("{\"dailyRate\": 1000000000000000000, \"currency\": \"VND\"}")] // Overflow > numeric(18,0)
    [InlineData("{\"dailyRate\": 500000, \"currency\": \"USD\"}")] // Invalid currency
    public async Task UpdateRentalRate_InvalidRateValues_Returns400(string rentalRateJson)
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: []));

        var response = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate",
            new StringContent(rentalRateJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRentalRate_OwnerWithoutManagePermission_Succeeds()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        var ownerTokenNoPerms = secFactory.CreateHumanToken(owner, permissions: []);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerTokenNoPerms);

        var putPayload = new
        {
            dailyRate = 350_000m,
            weeklyRate = 2_100_000m,
            monthlyRate = (decimal?)null,
            currency = "VND"
        };

        var response = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updatedRate = await response.Content.ReadFromJsonAsync<RentalRateDto>();
        Assert.NotNull(updatedRate);
        Assert.Equal(350_000m, updatedRate.DailyRate);
        Assert.Equal(2_100_000m, updatedRate.WeeklyRate);
        Assert.Null(updatedRate.MonthlyRate); // Cleared
        Assert.Equal("VND", updatedRate.Currency);

        // Verify in DB
        await using var db = CreateDbContext();
        var listingInDb = await db.EquipmentListings.FindAsync(asset.EquipmentListingId);
        Assert.NotNull(listingInDb);
        Assert.Equal(350_000m, listingInDb.DailyRate);
        Assert.Equal(2_100_000m, listingInDb.WeeklyRate);
        Assert.Null(listingInDb.MonthlyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_AdminCrossOwner_Succeeds()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();

        var adminId = Guid.NewGuid();
        var adminToken = secFactory.CreateAdminToken(adminId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var putPayload = new
        {
            dailyRate = 999_000m,
            currency = "VND"
        };

        var response = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rate = await response.Content.ReadFromJsonAsync<RentalRateDto>();
        Assert.NotNull(rate);
        Assert.Equal(999_000m, rate.DailyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_OtherUserAndService_DeniedWith403()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        var putPayload = new { dailyRate = 500_000m, currency = "VND" };
        var putContent = new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json");

        // 1. Other human -> 403
        var otherUserId = Guid.NewGuid();
        var otherUserToken = secFactory.CreateHumanToken(otherUserId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherUserToken);
        var otherUserResp = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Forbidden, otherUserResp.StatusCode);

        // 2. Service principal -> 403
        var serviceToken = secFactory.CreateServiceToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        var svcResp = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Forbidden, svcResp.StatusCode);

        // 3. Anonymous -> 401
        client.DefaultRequestHeaders.Authorization = null;
        var anonResp = await client.PutAsync($"api/v1/inventory/equipment-listings/{asset.EquipmentListingId:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
    }

    [Fact]
    public async Task FaultInjection_CreateListing_AssetInsertFails_RollsBackListingAndAssetAtomically()
    {
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        const string failSerial = "FAIL-CREATE-ONE-SERIAL";
        await using var triggerConn = new NpgsqlConnection(_fixture.ConnectionString);
        await triggerConn.OpenAsync();

        await using var setupCmd = triggerConn.CreateCommand();
        setupCmd.CommandText = $"""
            CREATE OR REPLACE FUNCTION trg_fn_fail_create_listing_asset()
            RETURNS TRIGGER AS $$
            BEGIN
                IF NEW.serial_number = '{failSerial}' THEN
                    RAISE EXCEPTION 'Simulated failure on first asset insertion!';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_test_fail_create_listing_asset ON inventory.assets;
            CREATE TRIGGER trg_test_fail_create_listing_asset
                BEFORE INSERT ON inventory.assets
                FOR EACH ROW EXECUTE FUNCTION trg_fn_fail_create_listing_asset();
        """;
        await setupCmd.ExecuteNonQueryAsync();

        try
        {
            var payload = new
            {
                equipmentTypeId = Guid.NewGuid(),
                brandId = Guid.NewGuid(),
                name = "Atomic Create Fault Test Listing",
                model = "Atomic Create Fault Test Listing",
                serialNumber = failSerial,
                manufactureYear = 2024,
                schemaVersion = 1,
                specValues = new { sensor = "Full-Frame" },
                rentalRate = new
                {
                    dailyRate = 500_000m,
                    currency = "VND"
                }
            };

            var response = await client.PostAsync("api/v1/inventory/equipment-listings",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            // Assert that both the listing and the asset are rolled back atomically
            await using var db = CreateDbContext();
            var listingExists = await db.EquipmentListings.AnyAsync(l => l.Model == "Atomic Create Fault Test Listing");
            var assetExists = await db.Assets.AnyAsync(a => a.SerialNumber == failSerial);
            Assert.False(listingExists);
            Assert.False(assetExists);
        }
        finally
        {
            await using var cleanupCmd = triggerConn.CreateCommand();
            cleanupCmd.CommandText = "DROP TRIGGER IF EXISTS trg_test_fail_create_listing_asset ON inventory.assets; DROP FUNCTION IF EXISTS trg_fn_fail_create_listing_asset();";
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task FaultInjection_AddAsset_AssetInsertFails_PreservesListingAndExistingUnits()
    {
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Create a valid listing with unit 1
        var createPayload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Atomic Add Fault Test Listing",
            model = "Atomic Add Fault Test Listing",
            serialNumber = "VALID-ADD-ONE-UNIT1",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new
            {
                dailyRate = 500_000m,
                currency = "VND"
            }
        };

        var createResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var listing = await createResp.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(listing);

        // 2. Setup trigger for failed 2nd unit
        const string failSerial = "FAIL-ADD-ONE-SERIAL";
        await using var triggerConn = new NpgsqlConnection(_fixture.ConnectionString);
        await triggerConn.OpenAsync();

        await using var setupCmd = triggerConn.CreateCommand();
        setupCmd.CommandText = $"""
            CREATE OR REPLACE FUNCTION trg_fn_fail_add_one_asset()
            RETURNS TRIGGER AS $$
            BEGIN
                IF NEW.serial_number = '{failSerial}' THEN
                    RAISE EXCEPTION 'Simulated failure on add asset insertion!';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_test_fail_add_one_asset ON inventory.assets;
            CREATE TRIGGER trg_test_fail_add_one_asset
                BEFORE INSERT ON inventory.assets
                FOR EACH ROW EXECUTE FUNCTION trg_fn_fail_add_one_asset();
        """;
        await setupCmd.ExecuteNonQueryAsync();

        try
        {
            var addPayload = new { serialNumber = failSerial };
            var addResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets",
                new StringContent(JsonSerializer.Serialize(addPayload), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, addResp.StatusCode);

            // 3. Assert listing and unit 1 are preserved, unit 2 was not saved
            await using var db = CreateDbContext();
            var listingInDb = await db.EquipmentListings.SingleOrDefaultAsync(l => l.Id == listing.Id);
            Assert.NotNull(listingInDb);

            var unitsInDb = await db.Assets.Where(a => a.EquipmentListingId == listing.Id).ToListAsync();
            Assert.Single(unitsInDb);
            Assert.Equal("VALID-ADD-ONE-UNIT1", unitsInDb[0].SerialNumber);

            var failedAssetExists = await db.Assets.AnyAsync(a => a.SerialNumber == failSerial);
            Assert.False(failedAssetExists);
        }
        finally
        {
            await using var cleanupCmd = triggerConn.CreateCommand();
            cleanupCmd.CommandText = "DROP TRIGGER IF EXISTS trg_test_fail_add_one_asset ON inventory.assets; DROP FUNCTION IF EXISTS trg_fn_fail_add_one_asset();";
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }
}
