using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task GetAssets_DynamicFilter_Number_GteAndLte_PostgreSqlJsonb()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("capacityKg", "Capacity", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed 3 assets with different JSONB capacityKg
        await SeedAssetAsync(ownerId, model: "Excavator 3000", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": 3000}");
        await SeedAssetAsync(ownerId, model: "Excavator 2000", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": 2000}");
        await SeedAssetAsync(ownerId, model: "Excavator 2500", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": 2500}");

        // Filter capacityKg >= 2500
        var filterJson = Uri.EscapeDataString("[{\"field\":\"capacityKg\",\"operator\":\"gte\",\"value\":2500}]");
        var response = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Equal(2, paged.TotalCount);
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal(2, paged.Items.Count);
    }

    [Fact]
    public async Task GetAssets_DynamicFilter_StringAndBoolean_PostgreSqlJsonb()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("fuelType", "Fuel", "text", true, true, false, 0, null, null, null, null, null),
                        new CatalogBatchFieldResponse("hasGps", "GPS", "boolean", true, true, false, 1, null, null, null, null, null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var a1 = await SeedAssetAsync(ownerId, model: "Truck Diesel GPS", serial: "SN-GPS-1", eqTypeId: eqTypeId, specValues: "{\"fuelType\":\"diesel\",\"hasGps\":true}");
        var a2 = await SeedAssetAsync(ownerId, model: "Truck Diesel NoGPS", serial: "SN-NOGPS-2", eqTypeId: eqTypeId, specValues: "{\"fuelType\":\"diesel\",\"hasGps\":false}");
        var a3 = await SeedAssetAsync(ownerId, model: "Truck Electric GPS", serial: "SN-ELEC-3", eqTypeId: eqTypeId, specValues: "{\"fuelType\":\"electric\",\"hasGps\":true}");

        var filterJson = Uri.EscapeDataString("[{\"field\":\"fuelType\",\"operator\":\"eq\",\"value\":\"diesel\"},{\"field\":\"hasGps\",\"operator\":\"eq\",\"value\":true}]");
        var response = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged.TotalCount);
        Assert.Equal(a1.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetAssets_DynamicFilter_MissingField_JsonNull_TypeMismatch_NeverFailsAndNeverMatches()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("capacityKg", "Capacity", "number", true, true, false, 0, 0, 10000, null, "kg", null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Missing field in JSONB
        await SeedAssetAsync(ownerId, model: "Missing Field", eqTypeId: eqTypeId, specValues: "{}");
        // 2. JSON null stored
        await SeedAssetAsync(ownerId, model: "Json Null", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": null}");
        // 3. String stored instead of number (wrong type)
        await SeedAssetAsync(ownerId, model: "String Stored", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": \"not-a-number\"}");
        // 4. Valid matching number
        var valid = await SeedAssetAsync(ownerId, model: "Valid Number", eqTypeId: eqTypeId, specValues: "{\"capacityKg\": 3000}");

        var filterJson = Uri.EscapeDataString("[{\"field\":\"capacityKg\",\"operator\":\"gte\",\"value\":2500}]");
        var response = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged.TotalCount);
        Assert.Equal(valid.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetAssets_ManufactureYearNull_AlwaysSortedAtEnd()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var a2022 = await SeedAssetAsync(ownerId, model: "Year 2022", year: 2022);
        var a2024 = await SeedAssetAsync(ownerId, model: "Year 2024", year: 2024);
        var aNull = await SeedAssetAsync(ownerId, model: "Year Null", year: null);

        // Sort DESC: 2024, 2022, Null
        var descResp = await client.GetAsync("api/v1/inventory/assets?sort=manufacture_year_desc");
        Assert.Equal(HttpStatusCode.OK, descResp.StatusCode);
        var descPaged = await descResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(descPaged);
        Assert.Equal(3, descPaged.Items.Count);
        Assert.Equal(a2024.Id, descPaged.Items[0].Id);
        Assert.Equal(a2022.Id, descPaged.Items[1].Id);
        Assert.Equal(aNull.Id, descPaged.Items[2].Id);

        // Sort ASC: 2022, 2024, Null
        var ascResp = await client.GetAsync("api/v1/inventory/assets?sort=manufacture_year_asc");
        Assert.Equal(HttpStatusCode.OK, ascResp.StatusCode);
        var ascPaged = await ascResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(ascPaged);
        Assert.Equal(3, ascPaged.Items.Count);
        Assert.Equal(a2022.Id, ascPaged.Items[0].Id);
        Assert.Equal(a2024.Id, ascPaged.Items[1].Id);
        Assert.Equal(aNull.Id, ascPaged.Items[2].Id);
    }

    [Fact]
    public async Task GetAssets_Search_Keyword_ModelOrSerialSubstring_WildcardLiteral()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var a1 = await SeedAssetAsync(ownerId, model: "Zoom 100% Optical", serial: "SN-LIT-01");
        var a2 = await SeedAssetAsync(ownerId, model: "Standard Camera", serial: "SN_SPECIAL_01");

        // Literal '%'
        var respPercent = await client.GetAsync($"api/v1/inventory/assets?q={Uri.EscapeDataString("100%")}");
        Assert.Equal(HttpStatusCode.OK, respPercent.StatusCode);
        var pagedPercent = await respPercent.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(pagedPercent);
        Assert.Single(pagedPercent.Items);
        Assert.Equal(a1.Id, pagedPercent.Items[0].Id);

        // Literal '_'
        var respUnder = await client.GetAsync($"api/v1/inventory/assets?q={Uri.EscapeDataString("_SPECIAL_")}");
        Assert.Equal(HttpStatusCode.OK, respUnder.StatusCode);
        var pagedUnder = await respUnder.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(pagedUnder);
        Assert.Single(pagedUnder.Items);
        Assert.Equal(a2.Id, pagedUnder.Items[0].Id);
    }

    [Fact]
    public async Task GetAssets_PaginationAndFilteredCount()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("power", "Power", "number", true, true, false, 0, 0, 1000, null, "kW", null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed 5 matching assets and 2 non-matching assets
        for (int i = 1; i <= 5; i++)
        {
            await SeedAssetAsync(ownerId, model: $"Gen {i}", eqTypeId: eqTypeId, specValues: "{\"power\": 500}");
        }
        await SeedAssetAsync(ownerId, model: "Gen Low", eqTypeId: eqTypeId, specValues: "{\"power\": 100}");
        await SeedAssetAsync(Guid.NewGuid(), model: "Gen Other User", eqTypeId: eqTypeId, specValues: "{\"power\": 500}");

        var filterJson = Uri.EscapeDataString("[{\"field\":\"power\",\"operator\":\"gte\",\"value\":300}]");

        // Page 1 of size 2
        var respP1 = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, respP1.StatusCode);
        var paged1 = await respP1.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged1);
        Assert.Equal(5, paged1.TotalCount);
        Assert.Equal(3, paged1.TotalPages);
        Assert.Equal(2, paged1.Items.Count);

        // Page 3 of size 2
        var respP3 = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}&page=3&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, respP3.StatusCode);
        var paged3 = await respP3.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged3);
        Assert.Equal(5, paged3.TotalCount);
        Assert.Single(paged3.Items);
    }

    [Fact]
    public async Task GetAssets_SqlInjectionPayload_HandledSafely()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var asset = await SeedAssetAsync(ownerId, model: "Safe Asset");

        // SQL injection in search q
        var maliciousQ = "'; DROP TABLE \"inventory\".\"assets\"; --";
        var resp = await client.GetAsync($"api/v1/inventory/assets?q={Uri.EscapeDataString(maliciousQ)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Verify table still exists and data intact
        await using var db = CreateDbContext();
        var stillExists = await db.Assets.AnyAsync(a => a.Id == asset.Id);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task GetAssets_MultiVersionCompatibility_RealPostgres()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                2, // Current version is 2
                [
                    // Version 1 has unit "lbs" (incompatible with current unit "kg")
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("weight", "Weight", "number", true, true, false, 0, 0, 1000, null, "lbs", null)
                    ]),
                    // Version 2 has unit "kg"
                    new CatalogBatchSchemaItemResponse(2, [
                        new CatalogBatchFieldResponse("weight", "Weight", "number", true, true, false, 0, 0, 1000, null, "kg", null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Asset A: schema_version 1 (incompatible)
        await SeedAssetAsync(ownerId, model: "Model V1", eqTypeId: eqTypeId, schemaVersion: 1, specValues: "{\"weight\": 50}");
        // Asset B: schema_version 2 (compatible)
        var assetB = await SeedAssetAsync(ownerId, model: "Model V2", eqTypeId: eqTypeId, schemaVersion: 2, specValues: "{\"weight\": 50}");

        var filterJson = Uri.EscapeDataString("[{\"field\":\"weight\",\"operator\":\"gte\",\"value\":40}]");
        var resp = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var paged = await resp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Equal(1, paged.TotalCount);
        Assert.Equal(assetB.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetAssets_SingleBatchCall_NoNPlusOne()
    {
        int catalogBatchCallCount = 0;
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) =>
            {
                catalogBatchCallCount++;
                return Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                    id,
                    1,
                    [
                        new CatalogBatchSchemaItemResponse(1, [
                            new CatalogBatchFieldResponse("f1", "Field 1", "text", true, true, false, 0, null, null, null, null, null)
                        ])
                    ])));
            }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed 10 assets with schema version 1
        for (int i = 0; i < 10; i++)
        {
            await SeedAssetAsync(ownerId, model: $"Bulk Asset {i}", eqTypeId: eqTypeId, specValues: "{\"f1\":\"value\"}");
        }

        var filterJson = Uri.EscapeDataString("[{\"field\":\"f1\",\"operator\":\"eq\",\"value\":\"value\"}]");
        var resp = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Batch catalog call MUST be called exactly once
        Assert.Equal(1, catalogBatchCallCount);
    }

    [Fact]
    public async Task GetAssets_OwnerSchemaVersionLookup_ExcludesAssetsFilteredOutByAcceptingBookings()
    {
        var eqTypeId = Guid.NewGuid();
        IReadOnlyList<int>? requestedVersions = null;

        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) =>
            {
                requestedVersions = versions;
                return Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                    id,
                    2,
                    [
                        new CatalogBatchSchemaItemResponse(2, [
                            new CatalogBatchFieldResponse("power", "Power", "number", true, true, false, 0, 0, 1000, null, "kW", null)
                        ])
                    ])));
            }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Asset 1: acceptingBookings = false, schemaVersion = 1
        var a1 = await SeedAssetAsync(ownerId, model: "Disabled V1", eqTypeId: eqTypeId, schemaVersion: 1, specValues: "{\"power\": 50}");
        await using (var db = CreateDbContext())
        {
            var tracked = await db.Assets.FindAsync(a1.Id);
            tracked!.DisableBookings("Maintenance", FixedNow);
            await db.SaveChangesAsync();
        }

        // Asset 2: acceptingBookings = true, schemaVersion = 2
        await SeedAssetAsync(ownerId, model: "Active V2", eqTypeId: eqTypeId, schemaVersion: 2, specValues: "{\"power\": 50}");

        var filterJson = Uri.EscapeDataString("[{\"field\":\"power\",\"operator\":\"gte\",\"value\":40}]");
        var resp = await client.GetAsync($"api/v1/inventory/assets?equipmentTypeId={eqTypeId:D}&acceptingBookings=true&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(requestedVersions);
        // Only version 2 must be requested to Catalog! Version 1 (from disabled asset) is excluded by acceptingBookings=true!
        var v = Assert.Single(requestedVersions);
        Assert.Equal(2, v);
    }
}
