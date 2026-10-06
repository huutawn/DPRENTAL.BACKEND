using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class CatalogHttpIntegrationTests
{
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task GetPublicAssets_WithoutDynamicFilters_MakesZeroCatalogCalls_EvenDuringOutage()
    {
        await using var catalogServer = new LocalCatalogServer();
        catalogServer.Handler = _ => Task.FromResult(new MockHttpResponse(503, "Catalog is down"));

        var (app, _, _) = CreateFactory(catalogServer.BaseUrl);
        await using var _ = app;
        using var client = app.CreateClient();

        var assetId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        await using (var db = CreateDbContext())
        {
            var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;
            var asset = InventoryAsset.Create(
                assetId, ownerId, Guid.NewGuid(), Guid.NewGuid(), "Outage Forklift", 1, "{}", FixedNow, rate).Value;
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        // 1. Public list without dynamic filters succeeds despite Catalog outage!
        var listRes = await client.GetAsync("api/v1/inventory/public/assets");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        Assert.Empty(catalogServer.CapturedRequests);

        // 2. Public detail succeeds despite Catalog outage!
        var detailRes = await client.GetAsync($"api/v1/inventory/public/assets/{assetId:D}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
        Assert.Empty(catalogServer.CapturedRequests);
    }

    [Fact]
    public async Task GetPublicAssets_WithDynamicFilters_SendsExactlyOneAnonymousBatchRequest()
    {
        await using var catalogServer = new LocalCatalogServer();
        var eqTypeId = Guid.NewGuid();

        catalogServer.Handler = req =>
        {
            if (req.Url?.AbsolutePath.EndsWith("/schemas/batch", StringComparison.Ordinal) == true)
            {
                var resp = new CatalogBatchSchemasResponse(
                    eqTypeId,
                    1,
                    [
                        new CatalogBatchSchemaItemResponse(1, [
                            new CatalogBatchFieldResponse("tonnage", "Tonnage", "number", true, true, false, 0, 0, 100, null, "tons", null)
                        ])
                    ]);
                var json = JsonSerializer.Serialize(resp, CamelCaseJsonOptions);
                return Task.FromResult(new MockHttpResponse(200, json));
            }
            return Task.FromResult(new MockHttpResponse(404));
        };

        var (app, _, _) = CreateFactory(catalogServer.BaseUrl);
        await using var _ = app;
        using var client = app.CreateClient();

        var assetId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        await using (var db = CreateDbContext())
        {
            var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;
            var asset = InventoryAsset.Create(
                assetId, ownerId, eqTypeId, Guid.NewGuid(), "Tonnage Forklift", 1, "{\"tonnage\": 5.0}", FixedNow, rate).Value;
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        var filterJson = Uri.EscapeDataString("[{\"field\":\"tonnage\",\"operator\":\"gte\",\"value\":4.0}]");
        var response = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);

        // Exactly one batch request sent!
        var batchReq = Assert.Single(catalogServer.CapturedRequests);
        Assert.Equal("POST", batchReq.Method);
        Assert.Contains($"/equipment-types/{eqTypeId:D}/schemas/batch", batchReq.PathAndQuery, StringComparison.Ordinal);
        Assert.Null(batchReq.Authorization); // Anonymous request!
    }

    [Fact]
    public async Task GetPublicAssets_WithDynamicFilters_CatalogOutage_Returns503()
    {
        await using var catalogServer = new LocalCatalogServer();
        catalogServer.Handler = _ => Task.FromResult(new MockHttpResponse(503, "Service Unavailable"));

        var (app, _, _) = CreateFactory(catalogServer.BaseUrl);
        await using var _ = app;
        using var client = app.CreateClient();

        var eqTypeId = Guid.NewGuid();
        var filterJson = Uri.EscapeDataString("[{\"field\":\"tonnage\",\"operator\":\"gte\",\"value\":4.0}]");
        var response = await client.GetAsync($"api/v1/inventory/public/assets?equipmentTypeId={eqTypeId:D}&filters={filterJson}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
