using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dprental.InventoryService.Api.Recommendation;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Recommendation;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

[Collection("ValkeyIntegrationCollection")]
public sealed class RecommendationHttpEndToEndIntegrationTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _postgres;
    private readonly ValkeyFixture _valkey;

    public RecommendationHttpEndToEndIntegrationTests(PostgresFixture postgresFixture, ValkeyFixture valkeyFixture)
    {
        _postgres = postgresFixture;
        _valkey = valkeyFixture;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_postgres.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private static async Task SeedWarehouseAsync(InventoryServiceDbContext db, Guid ownerUserId)
    {
        var exists = await db.Warehouses.AnyAsync(w => w.OwnerUserId == ownerUserId);
        if (!exists)
        {
            var wh = Warehouse.CreateDraft(ownerUserId, FixedNow);
            db.Warehouses.Add(wh);
            await db.SaveChangesAsync();
        }
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateTestApp(string? valkeyConn = null)
    {
        var testClock = new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_postgres.ConnectionString, testClock);

        string recommendationConn = valkeyConn ?? _valkey.ConnectionString;

        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:RecommendationRedis", recommendationConn);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(testClock);
            });
        });

        return (app, secFactory);
    }

    [Fact]
    public async Task Anonymous_PublicListing_Recommended_UsesGlobalRanking()
    {
        await using var db = CreateDbContext();
        var (app, _) = CreateTestApp();
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var brandId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        var listing1 = EquipmentListing.Create(
            Guid.NewGuid(), owner, typeId, brandId, "Anon Listing 1", null, "Model Anon 1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing1.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var listing2 = EquipmentListing.Create(
            Guid.NewGuid(), owner, typeId, brandId, "Anon Listing 2", null, "Model Anon 2", 1, "{}", 100_000m, null, null, "VND", FixedNow.AddSeconds(-5), 2024).Value;
        listing2.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow.AddSeconds(-5));

        db.EquipmentListings.AddRange(listing1, listing2);
        await db.SaveChangesAsync();

        // Populate global popularity: listing2 is more popular than listing1
        var valkeyDb = _valkey.Multiplexer.GetDatabase();
        string globalKey = "rec:v1:global:popular-listings";
        await valkeyDb.SortedSetAddAsync(globalKey, listing1.Id.ToString("D"), 10);
        await valkeyDb.SortedSetAddAsync(globalKey, listing2.Id.ToString("D"), 100);

        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("postgres", Assert.Single(response.Headers.GetValues("X-Search-Backend")));

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);
        Assert.Equal(2, paged.TotalCount);
        // Global popularity puts listing2 before listing1
        Assert.Equal(listing2.Id, paged.Items[0].Id);
        Assert.Equal(listing1.Id, paged.Items[1].Id);
    }

    [Fact]
    public async Task AuthenticatedHuman_PublicListing_Recommended_UsesPersonalizedAndExcludesOwn()
    {
        await using var db = CreateDbContext();
        var (app, secFactory) = CreateTestApp();
        using var client = app.CreateClient();

        var myUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedWarehouseAsync(db, myUserId);
        await SeedWarehouseAsync(db, otherUserId);

        var brandId = Guid.NewGuid();
        var typeFav = Guid.NewGuid();
        var typeOther = Guid.NewGuid();

        // 1. My listing (should be excluded)
        var myListing = EquipmentListing.Create(
            Guid.NewGuid(), myUserId, typeFav, brandId, "My Listing", null, "My Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        myListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        // 2. Other user's favorite type listing
        var favListing = EquipmentListing.Create(
            Guid.NewGuid(), otherUserId, typeFav, brandId, "Fav Listing", null, "Fav Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        favListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        // 3. Other user's normal listing
        var normalListing = EquipmentListing.Create(
            Guid.NewGuid(), otherUserId, typeOther, brandId, "Normal Listing", null, "Normal Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        normalListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.AddRange(myListing, favListing, normalListing);
        await db.SaveChangesAsync();

        // Set personal interests in Valkey
        var valkeyDb = _valkey.Multiplexer.GetDatabase();
        string userKey = $"rec:v1:user:{myUserId:D}:equipment-types";
        await valkeyDb.SortedSetAddAsync(userKey, typeFav.ToString("D"), 50);

        string token = secFactory.CreateHumanToken(myUserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);
        var ids = paged.Items.Select(x => x.Id).ToList();

        // My listing must be excluded
        Assert.DoesNotContain(myListing.Id, ids);
        // Favorite listing comes before normal listing
        Assert.Equal(favListing.Id, ids[0]);
        Assert.Equal(normalListing.Id, ids[1]);
    }

    [Fact]
    public async Task KeywordWithRecommended_NormalizesToRelevance_DoesNotApplyOwnerExclusion()
    {
        await using var db = CreateDbContext();
        var (app, secFactory) = CreateTestApp();
        using var client = app.CreateClient();

        var myUserId = Guid.NewGuid();
        await SeedWarehouseAsync(db, myUserId);
        var brandId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        string uniqueKeyword = $"CameraSpecial{Guid.NewGuid():N}";

        var myListing = EquipmentListing.Create(
            Guid.NewGuid(), myUserId, typeId, brandId, uniqueKeyword, null, uniqueKeyword, 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        myListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.Add(myListing);
        await db.SaveChangesAsync();

        string token = secFactory.CreateHumanToken(myUserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // When q is provided with sort=recommended, it normalizes to relevance and does NOT apply recommendation owner exclusion
        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?q={uniqueKeyword}&sort=recommended");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(myListing.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task ValkeyUnavailable_Returns200WithDefaultRanking()
    {
        await using var db = CreateDbContext();
        // Give an unreachable Valkey port (fail-fast 100ms)
        var (app, _) = CreateTestApp(valkeyConn: "127.0.0.1:44444");
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var brandId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        var listing = EquipmentListing.Create(
            Guid.NewGuid(), owner, typeId, brandId, "Fallback Listing", null, "Model Fallback", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.Add(listing);
        await db.SaveChangesAsync();

        var response = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged);
        Assert.Single(paged.Items);
        Assert.Equal(listing.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task HealthEndpoints_RemainHealthy_EvenWhenRecommendationValkeyIsUnreachable()
    {
        var (app, _) = CreateTestApp(valkeyConn: "127.0.0.1:44444");
        using var client = app.CreateClient();

        var liveRes = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveRes.StatusCode);

        var readyRes = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readyRes.StatusCode);
    }
}
