using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Media;
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

public sealed class EquipmentListingBatchIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public EquipmentListingBatchIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TrackingCatalogClient : ICatalogServiceClient
    {
        public int TotalCalls { get; private set; }

        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));
        }

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));
        }

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(Result.Success());
        }

    }

    private sealed class TrackingMediaClient : IMediaServiceClient
    {
        public int TotalCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            TotalCalls++;
            return Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
        }
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

    private async Task<EquipmentListing> CreateTestListingAsync(
        Guid ownerId,
        string name = "Sony FX3",
        decimal dailyRate = 500_000m,
        EquipmentListingStatus status = EquipmentListingStatus.Active,
        Guid? thumbnailMediaId = null,
        decimal depositAmount = 500_000m)
    {
        await using var db = CreateDbContext();
        var mediaIds = thumbnailMediaId.HasValue ? new[] { thumbnailMediaId.Value } : null;
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            name,
            "Internal detailed description that should NOT be in batch response",
            "FX3",
            1,
            "{\"sensor\":\"Full-Frame\",\"resolution\":\"4K\"}",
            dailyRate,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow,
            2024,
            mediaIds: mediaIds,
            thumbnailMediaId: thumbnailMediaId,
            depositAmount: depositAmount).Value;

        if (!await db.Warehouses.AnyAsync(w => w.OwnerUserId == ownerId))
        {
            db.Warehouses.Add(Warehouse.CreateWithAddress(ownerId, "123 Test Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, FixedNow).Value);
        }
        listing.UpdateStatus(status, true, FixedNow);
        db.EquipmentListings.Add(listing);

        var asset = InventoryAsset.Create(Guid.NewGuid(), listing.Id, $"SN-{listing.Id:N}", FixedNow).Value;
        db.Assets.Add(asset);

        await db.SaveChangesAsync();
        return listing;
    }

    [Fact]
    public async Task Batch_MissingApiKey_Returns401Unauthorized()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new EquipmentListingBatchRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_HumanToken_Returns403Forbidden(bool isAdmin)
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var userId = Guid.NewGuid();
        var token = isAdmin
            ? secFactory.CreateAdminToken(userId)
            : secFactory.CreateHumanToken(userId, permissions: ["inventory.asset.manage"]);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new EquipmentListingBatchRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Batch_ValidApiKeyWithoutServiceToken_Succeeds()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new EquipmentListingBatchRequest([Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"equipmentListingIds\": null}")]
    [InlineData("{\"equipmentListingIds\": []}")]
    [InlineData("{\"equipmentListingIds\": [\"00000000-0000-0000-0000-000000000000\"]}")]
    [InlineData("{\"equipmentListingIds\": [\"not-a-valid-guid\"]}")]
    public async Task Batch_InvalidRequestBody_Returns400BadRequest(string jsonPayload)
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var serviceToken = secFactory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/equipment-listings/batch")
        {
            Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_Exceeds100Ids_Returns400BadRequest()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var serviceToken = secFactory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new EquipmentListingBatchRequest(ids));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_ValidServiceToken_ReturnsMinimalContract_WithoutLeakingInternalOrPhysicalAssetData()
    {
        var (app, secFactory, catalog, media) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();

        var thumbnailId = Guid.NewGuid();
        var listing1 = await CreateTestListingAsync(owner1, name: "Sony FX3", dailyRate: 500_000m, status: EquipmentListingStatus.Active, thumbnailMediaId: thumbnailId);
        var listing2 = await CreateTestListingAsync(owner2, name: "Canon R5 C", dailyRate: 600_000m, status: EquipmentListingStatus.Inactive);
        var listing3 = await CreateTestListingAsync(owner1, name: "Draft Gear", dailyRate: 400_000m, status: EquipmentListingStatus.Draft);
        var missingId = Guid.NewGuid();

        var serviceToken = secFactory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // Send with duplicate of listing1 and mixed missing
        var requestPayload = new EquipmentListingBatchRequest([listing2.Id, listing1.Id, missingId, listing1.Id, listing3.Id]);

        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            requestPayload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("items", out var itemsElement));
        Assert.True(root.TryGetProperty("missingIds", out var missingIdsElement));

        // 3 items returned (deduplicated)
        Assert.Equal(3, itemsElement.GetArrayLength());
        // 1 missing ID returned
        Assert.Equal(1, missingIdsElement.GetArrayLength());
        Assert.Equal(missingId, missingIdsElement[0].GetGuid());

        // First seen ordering: listing2, listing1, listing3
        var item0 = itemsElement[0];
        Assert.Equal(listing2.Id, item0.GetProperty("id").GetGuid());
        Assert.Equal(owner2, item0.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(listing2.EquipmentTypeId, item0.GetProperty("equipmentTypeId").GetGuid());
        Assert.Equal("Canon R5 C", item0.GetProperty("name").GetString());
        Assert.Equal("VND", item0.GetProperty("currency").GetString());
        Assert.Equal("Inactive", item0.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item0.GetProperty("thumbnailMediaId").ValueKind);
        Assert.Equal(500_000m, item0.GetProperty("depositAmount").GetDecimal());

        var item1 = itemsElement[1];
        Assert.Equal(listing1.Id, item1.GetProperty("id").GetGuid());
        Assert.Equal(owner1, item1.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(listing1.EquipmentTypeId, item1.GetProperty("equipmentTypeId").GetGuid());
        Assert.Equal("Sony FX3", item1.GetProperty("name").GetString());
        Assert.Equal(500_000m, item1.GetProperty("dailyRate").GetDecimal());
        Assert.Equal("VND", item1.GetProperty("currency").GetString());
        Assert.Equal("Active", item1.GetProperty("status").GetString());

        Assert.Equal(500_000m, item1.GetProperty("depositAmount").GetDecimal());
        var item2 = itemsElement[2];
        Assert.Equal(listing3.Id, item2.GetProperty("id").GetGuid());
        Assert.Equal(listing3.EquipmentTypeId, item2.GetProperty("equipmentTypeId").GetGuid());
        Assert.Equal("Draft", item2.GetProperty("status").GetString());
        // Strict verification: Ensure NONE of the omitted fields exist in JSON response items
        var forbiddenFieldNames = new[]
        {
            "description",
            "schemaVersion",
            "specValues",
            "model",
            "brandId",
            "manufactureYear",
            "media",
            "thumbnailUrl",
            "updatedAt",
            "weeklyRate",
            "monthlyRate",
            "rentalRate",
            "totalQuantity",
            "availableQuantity",
            "serialNumber",
            "assetCode",
            "calendarBlocks",
            "allocations",
            "version",
            "xmin"
        };

        foreach (var item in itemsElement.EnumerateArray())
        {
            foreach (var forbidden in forbiddenFieldNames)
            {
                Assert.False(
                    item.TryGetProperty(forbidden, out var ignoredProp),
                    $"Property '{forbidden}' must NOT be present in batch item response.");
            }
        }

        // Verify downstream isolation: 0 calls to Catalog or Media services
        Assert.Equal(0, catalog.TotalCalls);
        Assert.Equal(0, media.TotalCalls);
    }

    [Fact]
    public async Task Batch_AllMissingIds_Returns200WithEmptyItemsAndAllMissingIds()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();

        var serviceToken = secFactory.CreateServiceToken(
            Guid.NewGuid(),
            scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new EquipmentListingBatchRequest([m1, m2, m1]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<EquipmentListingBatchResponse>();
        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(2, result.MissingIds.Count);
        Assert.Equal(m1, result.MissingIds[0]);
        Assert.Equal(m2, result.MissingIds[1]);
    }

    private sealed class CommandCountInterceptor : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Batch_DatabaseQueryCount_IsConstantAndDoesNotScaleWithNumberOfIds()
    {
        var ownerId = Guid.NewGuid();
        var listings = new List<EquipmentListing>();
        for (int i = 0; i < 5; i++)
        {
            listings.Add(await CreateTestListingAsync(ownerId, name: $"Listing {i}"));
        }

        var interceptor = new CommandCountInterceptor();
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new InventoryServiceDbContext(options);
        var repo = new EquipmentListingRepository(db);

        // 1 ID query: exactly 1 SELECT
        var result1 = await repo.GetBatchByIdsAsync([listings[0].Id]);
        Assert.Single(result1);
        Assert.Equal(1, interceptor.SelectCount);

        // 5 IDs query: still exactly 1 additional SELECT (total 2)
        var result5 = await repo.GetBatchByIdsAsync(listings.Select(l => l.Id).ToList());
        Assert.Equal(5, result5.Count);
        Assert.Equal(2, interceptor.SelectCount);
    }
}
