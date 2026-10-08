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
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class ListingPatchAndRateConcurrencyTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public ListingPatchAndRateConcurrencyTests(PostgresFixture fixture)
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
    }

    private sealed class TrackingMediaClient : IMediaServiceClient
    {
        public int VerifyCalls { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"))));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory, TrackingCatalogClient Catalog, TrackingMediaClient Media) CreateFactory(
        Func<IServiceProvider, IEquipmentListingRepository>? listingRepoFactory = null)
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

                if (listingRepoFactory != null)
                {
                    services.RemoveAll<IEquipmentListingRepository>();
                    services.AddScoped(listingRepoFactory);
                }
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

    private async Task<EquipmentListing> CreateTestListingAsync(Guid ownerId, Guid? thumbId = null)
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Original Sony FX3",
            1,
            "{\"resolution\":\"4K\"}",
            500_000m,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow,
            thumbId).Value;

        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        db.EquipmentListings.Add(listing);

        var asset = InventoryAsset.Create(Guid.NewGuid(), listing.Id, "FX3-ORIG-001", FixedNow).Value;
        db.Assets.Add(asset);

        await db.SaveChangesAsync();
        return listing;
    }

    [Theory]
    [InlineData("{\"model\": null}", "INVALID_MODEL")]
    [InlineData("{\"schemaVersion\": null}", "INVALID_SCHEMA_VERSION")]
    [InlineData("{\"specValues\": null}", "INVALID_SPEC_VALUES")]
    [InlineData("{\"status\": null}", "INVALID_STATUS")]
    [InlineData("{\"brandId\": null}", "INVALID_BRAND_ID")]
    public async Task PatchListing_ExplicitNullOnRequiredFields_Returns400WithSpecificErrorCode(string jsonPayload, string expectedErrorCode)
    {
        var (app, secFactory, catalog, media) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent(jsonPayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedErrorCode, body);

        // Verify no downstream calls made
        Assert.Equal(0, catalog.BrandCalls);
        Assert.Equal(0, catalog.SpecCalls);
        Assert.Equal(0, media.VerifyCalls);

        // Verify DB unchanged
        await using var db = CreateDbContext();
        var currentListing = await db.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal("Original Sony FX3", currentListing.Model);
    }

    [Fact]
    public async Task PatchListing_InvalidModelWhitespace_FailsLocallyBeforeCallingMediaOrCatalog()
    {
        var (app, secFactory, catalog, media) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var newThumb = Guid.NewGuid();
        var patchPayload = new
        {
            model = "   ", // Invalid whitespace model
            thumbnailMediaId = newThumb // should NOT call media because model is invalid locally!
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_MODEL", body);

        // Crucial: Media preparation MUST NOT be called because local validation failed first!
        Assert.Equal(0, media.VerifyCalls);
        Assert.Equal(0, catalog.BrandCalls);
        Assert.Equal(0, catalog.SpecCalls);
    }

    [Fact]
    public async Task PatchListing_ThumbnailMediaIdNull_ClearsThumbnailSuccessfully()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var initialThumb = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId, initialThumb);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Send explicit null for thumbnailMediaId
        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent("{\"thumbnailMediaId\": null}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(updated);
        Assert.Null(updated.ThumbnailUrl);

        await using var db = CreateDbContext();
        var dbListing = await db.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Null(dbListing.ThumbnailMediaId);
    }

    [Fact]
    public async Task PatchListing_OmittedFields_KeepsExistingValues()
    {
        var (app, secFactory, _, _) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Only patch model, omit brandId, schemaVersion, specValues, status
        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}",
            new StringContent("{\"model\": \"Updated Model Name Only\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = CreateDbContext();
        var dbListing = await db.EquipmentListings.SingleAsync(l => l.Id == listing.Id);
        Assert.Equal("Updated Model Name Only", dbListing.Model);
        Assert.Equal(listing.BrandId, dbListing.BrandId);
        Assert.Equal(listing.SchemaVersion, dbListing.SchemaVersion);
        using var expectedSpec = JsonDocument.Parse(listing.SpecValues);
        using var actualSpec = JsonDocument.Parse(dbListing.SpecValues);
        Assert.Equal(expectedSpec.RootElement.GetProperty("resolution").GetString(), actualSpec.RootElement.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task PutRentalRate_StaleSnapshotVersion_Returns409ConflictAndPreservesWinnerUpdate()
    {
        var ownerId = Guid.NewGuid();
        var listing = await CreateTestListingAsync(ownerId);

        int readCount = 0;
        var (app, secFactory, _, _) = CreateFactory(listingRepoFactory: sp =>
            new InterceptingListingRepository(
                new EquipmentListingRepository(sp.GetRequiredService<InventoryServiceDbContext>()),
                onAfterGetById: async (l) =>
                {
                    if (Interlocked.Increment(ref readCount) == 1)
                    {
                        // Concurrent client directly updates rental rate in DB between snapshot read and write lock
                        await using var db2 = CreateDbContext();
                        var listingToUpdate = await db2.EquipmentListings.SingleAsync(x => x.Id == listing.Id);
                        listingToUpdate.UpdateRentalRate(888_000m, null, null, "VND", DateTimeOffset.UtcNow);
                        await db2.SaveChangesAsync();
                    }
                }));

        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new RentalRateDto(777_000m, null, null, "VND");
        var response = await client.PutAsync($"api/v1/inventory/equipment-listings/{listing.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("STALE_INVENTORY_WRITE", body);

        // Verify in DB that winner's price (888,000 VND) was preserved, not overwritten by stale request (777,000 VND)
        await using var db = CreateDbContext();
        var finalListing = await db.EquipmentListings.SingleAsync(x => x.Id == listing.Id);
        Assert.Equal(888_000m, finalListing.DailyRate);
    }

    private sealed class InterceptingListingRepository : IEquipmentListingRepository
    {
        private readonly IEquipmentListingRepository _inner;
        private readonly Func<EquipmentListing?, Task> _onAfterGetById;

        public InterceptingListingRepository(IEquipmentListingRepository inner, Func<EquipmentListing?, Task> onAfterGetById)
        {
            _inner = inner;
            _onAfterGetById = onAfterGetById;
        }

        public async Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var res = await _inner.GetByIdAsync(id, cancellationToken);
            await _onAfterGetById(res);
            return res;
        }

        public Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.GetByIdForOwnerAsync(id, ownerUserId, cancellationToken);

        public Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetPublicByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default) =>
            _inner.ListForOwnerAsync(ownerUserId, skip, take, cancellationToken);

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) =>
            _inner.CountForOwnerAsync(ownerUserId, cancellationToken);

        public Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            _inner.GetTotalQuantityAsync(listingId, cancellationToken);

        public Task<IReadOnlyList<int>> GetDistinctSchemaVersionsPublicAsync(AssetSearchFilters filters, CancellationToken cancellationToken = default) =>
            _inner.GetDistinctSchemaVersionsPublicAsync(filters, cancellationToken);

        public Task<PublicEquipmentListingSearchResult> SearchPublicAsync(EquipmentListingSearchCriteria criteria, CancellationToken cancellationToken = default) =>
            _inner.SearchPublicAsync(criteria, cancellationToken);

        public Task<EquipmentListing?> GetCalendarSnapshotAsync(Guid listingId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            _inner.GetCalendarSnapshotAsync(listingId, startDate, endDate, cancellationToken);

        public void Add(EquipmentListing listing) => _inner.Add(listing);
        public void Update(EquipmentListing listing) => _inner.Update(listing);
    }
}
