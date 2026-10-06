using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task GetPublicAssets_AnonymousCaller_SucceedsWith200AndWireContract()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var eqId = Guid.NewGuid();
        var thumbId = Guid.NewGuid();
        var asset = await SeedAssetAsync(
            ownerId,
            model: "Toyota 8FD25",
            brandId: brandId,
            eqTypeId: eqId,
            thumb: thumbId,
            year: 2023,
            specValues: "{\"capacityKg\": 2500}");

        var response = await client.GetAsync("api/v1/inventory/public/assets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("items", out var itemsElem));
        Assert.True(root.TryGetProperty("page", out var pageElem));
        Assert.True(root.TryGetProperty("pageSize", out var pageSizeElem));
        Assert.True(root.TryGetProperty("totalCount", out var totalCountElem));
        Assert.True(root.TryGetProperty("totalPages", out var totalPagesElem));

        Assert.Equal(1, pageElem.GetInt32());
        Assert.Equal(20, pageSizeElem.GetInt32());
        Assert.True(totalCountElem.GetInt32() >= 1);

        var firstItem = itemsElem.EnumerateArray().FirstOrDefault(i => i.GetProperty("id").GetGuid() == asset.Id);
        Assert.NotEqual(default, firstItem);
        Assert.Equal("Toyota 8FD25", firstItem.GetProperty("model").GetString());
        Assert.Equal(2023, firstItem.GetProperty("manufactureYear").GetInt32());
        Assert.True(firstItem.GetProperty("acceptingBookings").GetBoolean());
        Assert.True(firstItem.TryGetProperty("rentalRate", out var rateElem));
        Assert.Equal(100_000m, rateElem.GetProperty("dailyRate").GetDecimal());
        Assert.Equal("VND", rateElem.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task GetPublicAssets_SpansMultipleOwners_ReturnsOnlyAcceptingBookings()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var uniqueModelA = "PublicModelA_" + Guid.NewGuid().ToString("N");
        var uniqueModelB = "PublicModelB_" + Guid.NewGuid().ToString("N");
        var uniqueModelDisabled = "PublicModelDisabled_" + Guid.NewGuid().ToString("N");

        var assetA = await SeedAssetAsync(ownerA, model: uniqueModelA);
        var assetB = await SeedAssetAsync(ownerB, model: uniqueModelB);
        var assetDisabled = await SeedAssetAsync(ownerA, model: uniqueModelDisabled);

        await using (var db = CreateDbContext())
        {
            var tracked = await db.Assets.FindAsync(assetDisabled.Id);
            tracked!.DisableBookings("Maintenance", FixedNow);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("api/v1/inventory/public/assets?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);

        var models = paged.Items.Select(i => i.Model).ToList();
        Assert.Contains(uniqueModelA, models);
        Assert.Contains(uniqueModelB, models);
        Assert.DoesNotContain(uniqueModelDisabled, models);
    }

    [Fact]
    public async Task GetPublicAssetById_DisabledAssetAndNonexistentAsset_ProduceIdentical404Contract()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Disabled Forklift");
        await using (var db = CreateDbContext())
        {
            var tracked = await db.Assets.FindAsync(asset.Id);
            tracked!.DisableBookings("Offline", FixedNow);
            await db.SaveChangesAsync();
        }

        // 1. Nonexistent asset
        var resNonexistent = await client.GetAsync($"api/v1/inventory/public/assets/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, resNonexistent.StatusCode);
        var probNonexistent = await resNonexistent.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(probNonexistent);
        Assert.Equal("ASSET_NOT_FOUND", probNonexistent.Title);
        Assert.Equal("Tài sản không tồn tại.", probNonexistent.Detail);

        // 2. Disabled asset
        var resDisabled = await client.GetAsync($"api/v1/inventory/public/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, resDisabled.StatusCode);
        var probDisabled = await resDisabled.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(probDisabled);
        Assert.Equal("ASSET_NOT_FOUND", probDisabled.Title);
        Assert.Equal("Tài sản không tồn tại.", probDisabled.Detail);
        // 1. Empty GUID -> 404 ASSET_NOT_FOUND
        var resEmpty = await client.GetAsync($"api/v1/inventory/public/assets/{Guid.Empty:D}");
        Assert.Equal(HttpStatusCode.NotFound, resEmpty.StatusCode);

        // 2. Malformed GUID without auth -> 401 (FallbackPolicy)
        var resMalformedAnon = await client.GetAsync("api/v1/inventory/public/assets/not-a-valid-guid");
        Assert.Equal(HttpStatusCode.Unauthorized, resMalformedAnon.StatusCode);

        // 3. Malformed GUID with auth -> 404
        var token = secFactory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resMalformedAuth = await client.GetAsync("api/v1/inventory/public/assets/not-a-valid-guid");
        Assert.Equal(HttpStatusCode.NotFound, resMalformedAuth.StatusCode);

    }

    [Theory]
    [InlineData("ownerUserId", "3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("serialNumber", "SN-SECRET")]
    [InlineData("acceptingBookings", "true")]
    [InlineData("suspensionReason", "Maintenance")]
    public async Task GetPublicAssets_PrivateQueryParameters_Return400(string paramName, string paramVal)
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var response = await client.GetAsync($"api/v1/inventory/public/assets?{paramName}={paramVal}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
    }

    [Fact]
    public async Task GetPublicAssets_DuplicateQueryParameter_Returns400()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var response = await client.GetAsync("api/v1/inventory/public/assets?q=toyota&q=komatsu");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVALID_QUERY_PARAMETER", problem.Title);
        Assert.Contains("lặp lại", problem.Detail);
    }

    [Fact]
    public async Task GetPublicAssets_BeyondLastPage_ReturnsEmptyItemsWithCorrectTotal()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var uniqueModel = "BeyondPage_" + Guid.NewGuid().ToString("N");
        await SeedAssetAsync(owner, model: uniqueModel);

        var response = await client.GetAsync($"api/v1/inventory/public/assets?q={uniqueModel}&page=100&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicInventoryAssetResponse>>();
        Assert.NotNull(paged);
        Assert.Empty(paged.Items);
        Assert.Equal(1, paged.TotalCount);
        Assert.Equal(1, paged.TotalPages);
    }
}
