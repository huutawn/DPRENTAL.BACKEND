using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task Post_Success201_ReturnsCreatedAssetAndLocationHeader()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var token = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var eqTypeId = Guid.NewGuid();
        var payload = new
        {
            equipmentTypeId = eqTypeId,
            model = "Sony FX3 Cinema Line",
            schemaVersion = 1,
            specValues = new { resolution = "4K", fps = 120 },
            serialNumber = "SN-POST-01",
            manufactureYear = 2024
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("api/v1/inventory/assets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(ownerId, created.OwnerUserId);
        Assert.Equal(eqTypeId, created.EquipmentTypeId);
        Assert.Equal("Sony FX3 Cinema Line", created.Model);
        Assert.Equal("SN-POST-01", created.SerialNumber);
        Assert.Equal(2024, created.ManufactureYear);
        Assert.True(created.AcceptingBookings);
        Assert.Null(created.SuspensionReason);
        Assert.Equal(FixedNow, created.CreatedAt);

        // Location header check
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"api/v1/inventory/assets/{created.Id:D}", response.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);

        // Verify persisted in PostgreSQL
        await using var db = CreateDbContext();
        var dbAsset = await db.Assets.SingleOrDefaultAsync(a => a.Id == created.Id);
        Assert.NotNull(dbAsset);
        Assert.Equal(ownerId, dbAsset.OwnerUserId);
        Assert.Equal("Sony FX3 Cinema Line", dbAsset.Model);
        Assert.Equal(2024, dbAsset.ManufactureYear);
        Assert.True(dbAsset.Version > 0);
    }

    [Fact]
    public async Task GetList_ReturnsOnlyCurrentUserAssets_EvenForAdmin_WithPaging()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var assetA = await SeedAssetAsync(userA, model: "User A Model");
        var assetB = await SeedAssetAsync(userB, model: "User B Model");

        // User A list query
        var tokenA = secFactory.CreateHumanToken(userA, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var respA = await client.GetAsync("api/v1/inventory/assets?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);

        var pagedA = await respA.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(pagedA);
        Assert.Single(pagedA.Items);
        Assert.Equal(assetA.Id, pagedA.Items[0].Id);
        Assert.Equal(1, pagedA.TotalCount);
        Assert.Equal(1, pagedA.TotalPages);

        // Admin list query -> returns only admin's assets (0 items), not user A or user B!
        var tokenAdmin = secFactory.CreateAdminToken(adminId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenAdmin);

        var respAdmin = await client.GetAsync("api/v1/inventory/assets?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, respAdmin.StatusCode);

        var pagedAdmin = await respAdmin.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(pagedAdmin);
        Assert.Empty(pagedAdmin.Items);
        Assert.Equal(0, pagedAdmin.TotalCount);
    }

    [Fact]
    public async Task GetById_OwnAsset_Success200_CrossUser_Forbidden403_AdminBypass_Success200()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var admin = Guid.NewGuid();

        var asset = await SeedAssetAsync(owner, model: "Secret Equipment");

        // 1. Owner reads -> 200
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var ownResp = await client.GetAsync($"api/v1/inventory/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.OK, ownResp.StatusCode);

        // 2. Cross user reads -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(otherUser, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));
        var crossResp = await client.GetAsync($"api/v1/inventory/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, crossResp.StatusCode);

        // 3. Admin reads -> 200 (admin bypass for asset management)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateAdminToken(admin));
        var adminResp = await client.GetAsync($"api/v1/inventory/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);
    }

    [Fact]
    public async Task DownstreamCatalogNotFound_MapsTo400ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            LookupHandler = _ => Task.FromResult(Result.Failure<CatalogEquipmentTypeResponse>(
                Error.Validation("EQUIPMENT_TYPE_NOT_FOUND", "Loại thiết bị không tồn tại hoặc chưa được công khai.")))
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
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("EQUIPMENT_TYPE_NOT_FOUND", body);
    }

    [Fact]
    public async Task DownstreamCatalogUnavailable_MapsTo503ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            LookupHandler = _ => Task.FromResult(Result.Failure<CatalogEquipmentTypeResponse>(
                Error.Unavailable("CATALOG_SERVICE_TIMEOUT", "Hết thời gian chờ phản hồi từ CatalogService.")))
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
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CATALOG_SERVICE_TIMEOUT", body);
    }
}
