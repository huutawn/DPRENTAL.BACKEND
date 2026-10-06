using System.Net;
using System.Net.Http.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task GetPublicAssets_KeywordSearch_MatchesModelOnlyAndNotSerialNumber()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var uniqueModel = "PublicModel_" + Guid.NewGuid().ToString("N");
        var secretSerial = "SN_CONFIDENTIAL_" + Guid.NewGuid().ToString("N");

        await SeedAssetAsync(owner, model: uniqueModel, serial: secretSerial);

        // 1. Search by Model works
        var resModel = await client.GetAsync($"api/v1/inventory/public/assets?q={uniqueModel}");
        Assert.Equal(HttpStatusCode.OK, resModel.StatusCode);
        var pagedModel = await resModel.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(pagedModel);
        Assert.Single(pagedModel.Items);
        Assert.Equal(uniqueModel, pagedModel.Items[0].Model);

        // 2. Search by SerialNumber MUST NOT match in public discovery
        var resSerial = await client.GetAsync($"api/v1/inventory/public/assets?q={secretSerial}");
        Assert.Equal(HttpStatusCode.OK, resSerial.StatusCode);
        var pagedSerial = await resSerial.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(pagedSerial);
        Assert.Empty(pagedSerial.Items);
        Assert.Equal(0, pagedSerial.TotalCount);
    }

    [Fact]
    public async Task GetPublicAssets_LiteralWildcardEscaping_PreservesLiterals()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var uniquePrefix = "EscTest_" + Guid.NewGuid().ToString("N");
        var modelWithWildcard = $"{uniquePrefix}_100%_Special";
        var modelWithoutWildcard = $"{uniquePrefix}_100_Special";

        await SeedAssetAsync(owner, model: modelWithWildcard);
        await SeedAssetAsync(owner, model: modelWithoutWildcard);

        // Search with literal %
        var resPercent = await client.GetAsync($"api/v1/inventory/public/assets?q={Uri.EscapeDataString(uniquePrefix + "_100%")}");
        var pagedPercent = await resPercent.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(pagedPercent);
        Assert.Single(pagedPercent.Items);
        Assert.Equal(modelWithWildcard, pagedPercent.Items[0].Model);
    }

    [Fact]
    public async Task GetPublicAssets_MetadataFilters_EquipmentAndBrandAndYears()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var targetEq = Guid.NewGuid();
        var targetBrand = Guid.NewGuid();

        var assetMatch = await SeedAssetAsync(owner, model: "Match", eqTypeId: targetEq, brandId: targetBrand, year: 2022);
        var assetWrongEq = await SeedAssetAsync(owner, model: "WrongEq", eqTypeId: Guid.NewGuid(), brandId: targetBrand, year: 2022);
        var assetWrongBrand = await SeedAssetAsync(owner, model: "WrongBrand", eqTypeId: targetEq, brandId: Guid.NewGuid(), year: 2022);
        var assetWrongYear = await SeedAssetAsync(owner, model: "WrongYear", eqTypeId: targetEq, brandId: targetBrand, year: 2018);

        var query = $"api/v1/inventory/public/assets?equipmentTypeId={targetEq:D}&brandId={targetBrand:D}&manufactureYearFrom=2020&manufactureYearTo=2024";
        var res = await client.GetAsync(query);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var paged = await res.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(assetMatch.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetPublicAssets_DynamicFilter_NumberAndText_RealPostgreSqlJsonb()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("tonnage", "Tonnage", "number", true, true, false, 0, 0, 100, null, "tons", null),
                        new CatalogBatchFieldResponse("engine", "Engine", "text", true, true, false, 0, null, null, null, null, null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var a1 = await SeedAssetAsync(owner, model: "Forklift A", eqTypeId: eqTypeId, specValues: "{\"tonnage\": 5.5, \"engine\": \"diesel\"}");
        var a2 = await SeedAssetAsync(owner, model: "Forklift B", eqTypeId: eqTypeId, specValues: "{\"tonnage\": 2.0, \"engine\": \"diesel\"}");
        var a3 = await SeedAssetAsync(owner, model: "Forklift C", eqTypeId: eqTypeId, specValues: "{\"tonnage\": 7.0, \"engine\": \"electric\"}");

        // Filter: tonnage >= 5 AND engine eq diesel
        var filterJson = Uri.EscapeDataString("[{\"field\":\"tonnage\",\"operator\":\"gte\",\"value\":5.0},{\"field\":\"engine\",\"operator\":\"eq\",\"value\":\"diesel\"}]");
        var res = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var paged = await res.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(a1.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetPublicAssets_DynamicFilter_BooleanAndEnum_RealPostgreSqlJsonb()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("hasCabin", "Cabin", "boolean", true, true, false, 0, null, null, null, null, null),
                        new CatalogBatchFieldResponse("transmission", "Transmission", "enum", true, true, false, 0, null, null,
                            [new CatalogBatchOptionResponse("manual", "Manual"), new CatalogBatchOptionResponse("automatic", "Automatic")], null, null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var a1 = await SeedAssetAsync(owner, model: "Cabin Auto", eqTypeId: eqTypeId, specValues: "{\"hasCabin\": true, \"transmission\": \"automatic\"}");
        var a2 = await SeedAssetAsync(owner, model: "NoCabin Auto", eqTypeId: eqTypeId, specValues: "{\"hasCabin\": false, \"transmission\": \"automatic\"}");
        var a3 = await SeedAssetAsync(owner, model: "Cabin Manual", eqTypeId: eqTypeId, specValues: "{\"hasCabin\": true, \"transmission\": \"manual\"}");

        // Filter: hasCabin eq true AND transmission eq automatic
        var filterJson = Uri.EscapeDataString("[{\"field\":\"hasCabin\",\"operator\":\"eq\",\"value\":true},{\"field\":\"transmission\",\"operator\":\"eq\",\"value\":\"automatic\"}]");
        var res = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var paged = await res.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(a1.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetPublicAssets_DynamicFilter_HistoricalSchemaCompatibility_RealPostgreSqlJsonb()
    {
        var eqTypeId = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                2,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("fuelType", "Fuel", "enum", true, true, false, 0, null, null,
                            [new CatalogBatchOptionResponse("diesel", "Diesel")], null, null) // version 1 has only diesel!
                    ]),
                    new CatalogBatchSchemaItemResponse(2, [
                        new CatalogBatchFieldResponse("fuelType", "Fuel", "enum", true, true, false, 0, null, null,
                            [new CatalogBatchOptionResponse("diesel", "Diesel"), new CatalogBatchOptionResponse("electric", "Electric")], null, null)
                    ])
                ])))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var a1 = await SeedAssetAsync(owner, model: "V1 Diesel Only", eqTypeId: eqTypeId, schemaVersion: 1, specValues: "{\"fuelType\": \"electric\"}");
        var a2 = await SeedAssetAsync(owner, model: "V2 Electric", eqTypeId: eqTypeId, schemaVersion: 2, specValues: "{\"fuelType\": \"electric\"}");

        // Filter: fuelType eq electric
        var filterJson = Uri.EscapeDataString("[{\"field\":\"fuelType\",\"operator\":\"eq\",\"value\":\"electric\"}]");
        var res = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var paged = await res.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        // Only a2 is returned because version 1 is incompatible!
        Assert.Single(paged.Items);
        Assert.Equal(a2.Id, paged.Items[0].Id);
    }
}
