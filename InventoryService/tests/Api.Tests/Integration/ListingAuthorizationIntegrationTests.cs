using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class ListingAuthorizationIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public ListingAuthorizationIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TrackingCatalogClient : ICatalogServiceClient
    {
        public int LookupCalls { get; private set; }
        public int BrandCalls { get; private set; }
        public int SpecCalls { get; private set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            return Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));
        }

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default)
        {
            BrandCalls++;
            return Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            SpecCalls++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBatchSchemasResponse(equipmentTypeId, 1, [new CatalogBatchSchemaItemResponse(1, [])])));

        public Task<Result<IReadOnlyList<Guid>>> GetEquipmentTypeIdsByCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Guid>>(Array.Empty<Guid>()));
    }

    private sealed class TrackingMediaClient : IMediaServiceClient
    {
        public int VerifyCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory, TrackingCatalogClient Catalog, TrackingMediaClient Media) CreateFactory()
    {
        var testClock = new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
        var catalog = new TrackingCatalogClient();
        var media = new TrackingMediaClient();

        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(catalog);

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(media);
            });
        });
        return (app, secFactory, catalog, media);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<EquipmentListing> CreateTestListingAsync(Guid ownerId)
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
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        db.EquipmentListings.Add(listing);

        var asset = InventoryAsset.Create(Guid.NewGuid(), listing.Id, "FX3-AUTH-0001", FixedNow).Value;
        db.Assets.Add(asset);

        await db.SaveChangesAsync();
        return listing;
    }

    [Fact]
    public async Task ServicePrincipal_WithSubMatchingOwner_IsForbidden_OnAllListingManagementApis()
    {
        var (app, secFactory, catalog, media) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        // Service token with sub = ownerId and manage permission
        var serviceToken = secFactory.CreateServiceToken(
            serviceId: ownerId,
            scopes: [InventoryAccessEvaluator.ScopeAllocationWrite, InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. POST Create Listing -> 403 Forbidden
        var createPayload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Service Created Model",
            model = "Service Created Model",
            serialNumber = "FX3-SVC-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };
        var createResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, createResp.StatusCode);
        Assert.Equal(0, catalog.LookupCalls);
        Assert.Equal(0, media.VerifyCalls);

        // 2. GET List Listings -> 403 Forbidden
        var listResp = await client.GetAsync("api/v1/inventory/equipment-listings");
        Assert.Equal(HttpStatusCode.Forbidden, listResp.StatusCode);

        // 3. GET Listing by ID -> 403 Forbidden
        var getResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, getResp.StatusCode);

        // 4. PATCH Listing -> 403 Forbidden
        var patchResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent("{\"model\":\"Hacked Model\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, patchResp.StatusCode);

        // 5. PUT Rental Rate -> 403 Forbidden
        var putRatePayload = new { dailyRate = 999_999m, currency = "VND" };
        var putResp = await client.PutAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putRatePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, putResp.StatusCode);

        // 6. POST Add Asset -> 403 Forbidden
        var addAssetPayload = new { serialNumber = "FX3-SVC-002" };
        var addResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets",
            new StringContent(JsonSerializer.Serialize(addAssetPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, addResp.StatusCode);

        // 7. GET Listing Assets -> 403 Forbidden
        var assetsResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets");
        Assert.Equal(HttpStatusCode.Forbidden, assetsResp.StatusCode);

        // 8. GET Listing Calendar -> 403 Forbidden
        var calResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.Forbidden, calResp.StatusCode);

        // Verify Database has NO mutations from the service calls
        await using var db = CreateDbContext();
        var currentListing = await db.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal("Sony FX3 Cinema Line", currentListing.Model);
        Assert.Equal(500_000m, currentListing.DailyRate);
        var unitCount = await db.Assets.CountAsync(a => a.EquipmentListingId == listing.Id);
        Assert.Equal(1, unitCount);
    }

    [Fact]
    public async Task AnonymousCaller_IsUnauthorized401_OnAllListingManagementApis()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        // No Authorization header
        var createResp = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, createResp.StatusCode);

        var listResp = await client.GetAsync("api/v1/inventory/equipment-listings");
        Assert.Equal(HttpStatusCode.Unauthorized, listResp.StatusCode);

        var getResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        var patchResp = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, patchResp.StatusCode);

        var putResp = await client.PutAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/rental-rate",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, putResp.StatusCode);

        var addResp = await client.PostAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, addResp.StatusCode);

        var assetsResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/assets");
        Assert.Equal(HttpStatusCode.Unauthorized, assetsResp.StatusCode);

        var calResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar");
        Assert.Equal(HttpStatusCode.Unauthorized, calResp.StatusCode);
    }

    [Fact]
    public async Task ValidHumanOwnerAndAdmin_CanManageListing_Successfully()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        // 1. Human Owner can read detail and calendar
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var getResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var calResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, calResp.StatusCode);

        // 2. Human Admin can also read and manage
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateAdminToken(adminId));

        var adminGetResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}");
        Assert.Equal(HttpStatusCode.OK, adminGetResp.StatusCode);

        var adminCalResp = await client.GetAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/calendar?from=2026-11-10&to=2026-11-12");
        Assert.Equal(HttpStatusCode.OK, adminCalResp.StatusCode);
    }
}
