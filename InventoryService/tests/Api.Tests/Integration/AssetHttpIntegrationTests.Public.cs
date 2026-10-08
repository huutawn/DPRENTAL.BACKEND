using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Common.Enums;
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

        var response = await client.GetAsync("api/v1/inventory/public/equipment-listings");

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

        var firstItem = itemsElem.EnumerateArray().FirstOrDefault(i => i.GetProperty("id").GetGuid() == asset.EquipmentListingId);
        Assert.NotEqual(default, firstItem);
        Assert.Equal("Toyota 8FD25", firstItem.GetProperty("model").GetString());
        Assert.True(firstItem.TryGetProperty("totalQuantity", out var totalQtyElem));
        Assert.True(totalQtyElem.GetInt32() >= 1);
        Assert.True(firstItem.TryGetProperty("availableQuantity", out var availQtyElem));
        Assert.True(availQtyElem.GetInt32() >= 1);
        Assert.True(firstItem.TryGetProperty("rentalRate", out var rateElem));
        Assert.Equal(100_000m, rateElem.GetProperty("dailyRate").GetDecimal());
        Assert.Equal("VND", rateElem.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task GetPublicAssets_ReturnsOnlyActiveListings()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var uniqueModelA = "Public_Active_A_" + Guid.NewGuid().ToString("N");
        var uniqueModelB = "Public_Active_B_" + Guid.NewGuid().ToString("N");
        var uniqueModelDraft = "Public_Draft_" + Guid.NewGuid().ToString("N");

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerA, model: uniqueModelA, status: EquipmentListingStatus.Active);
            await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerB, model: uniqueModelB, status: EquipmentListingStatus.Active);
            await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: ownerA, model: uniqueModelDraft, status: EquipmentListingStatus.Draft);
        }

        var response = await client.GetAsync("api/v1/inventory/public/equipment-listings?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);

        var models = paged.Items.Select(i => i.Model).ToList();
        Assert.Contains(uniqueModelA, models);
        Assert.Contains(uniqueModelB, models);
        Assert.DoesNotContain(uniqueModelDraft, models);
    }

    [Fact]
    public async Task GetPublicAssetById_DraftListingAndNonexistentListing_ProduceIdentical404Contract()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        Guid draftListingId;
        await using (var db = CreateDbContext())
        {
            var listing = await IntegrationTestHelper.InsertTestListingAsync(db, ownerId: owner, model: "Draft Listing", status: EquipmentListingStatus.Draft);
            draftListingId = listing.Id;
        }

        // 1. Nonexistent listing
        var resNonexistent = await client.GetAsync($"api/v1/inventory/public/equipment-listings/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, resNonexistent.StatusCode);
        var probNonexistent = await resNonexistent.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(probNonexistent);
        Assert.Equal("LISTING_NOT_FOUND", probNonexistent.Title);
        Assert.Equal("Listing không tồn tại.", probNonexistent.Detail);

        // 2. Draft listing
        var resDraft = await client.GetAsync($"api/v1/inventory/public/equipment-listings/{draftListingId:D}");
        Assert.Equal(HttpStatusCode.NotFound, resDraft.StatusCode);
        var probDraft = await resDraft.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(probDraft);
        Assert.Equal("LISTING_NOT_FOUND", probDraft.Title);
        Assert.Equal("Listing không tồn tại.", probDraft.Detail);

        // 3. Empty GUID -> 404 LISTING_NOT_FOUND
        var resEmpty = await client.GetAsync($"api/v1/inventory/public/equipment-listings/{Guid.Empty:D}");
        Assert.Equal(HttpStatusCode.NotFound, resEmpty.StatusCode);

        // 4. Malformed GUID without auth -> 401 (FallbackPolicy)
        var resMalformedAnon = await client.GetAsync("api/v1/inventory/public/equipment-listings/not-a-valid-guid");
        Assert.Equal(HttpStatusCode.Unauthorized, resMalformedAnon.StatusCode);
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

        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?{paramName}={paramVal}");

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

        var response = await client.GetAsync("api/v1/inventory/public/equipment-listings?q=toyota&q=komatsu");

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

        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?q={uniqueModel}&page=100&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);
        Assert.Empty(paged.Items);
        Assert.Equal(1, paged.TotalCount);
        Assert.Equal(1, paged.TotalPages);
    }
}
