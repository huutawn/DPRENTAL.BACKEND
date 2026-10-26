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
public sealed partial class RecommendationHttpEndToEndIntegrationTests : IClassFixture<PostgresFixture>
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

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateTestApp(string? valkeyConn = null, DateTimeOffset? now = null)
    {
        var testClock = new TestTimeProvider(now ?? FixedNow);
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

    private async Task ResetValkeyAsync()
    {
        var endpoints = _valkey.Multiplexer.GetEndPoints();
        foreach (var ep in endpoints)
        {
            var server = _valkey.Multiplexer.GetServer(ep);
            if (server.IsConnected)
            {
                await server.FlushDatabaseAsync();
            }
        }
    }

    [Fact]
    public async Task Anonymous_PublicListing_Recommended_UsesGlobalRanking()
    {
        await ResetValkeyAsync();
        await using var db = CreateDbContext();
        var (app, _) = CreateTestApp();
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var brandId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        // 5 listings with fixed IDs
        var idA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
        var idB = Guid.Parse("b0000000-0000-0000-0000-000000000002");
        var idC = Guid.Parse("c0000000-0000-0000-0000-000000000003");
        var idD = Guid.Parse("d0000000-0000-0000-0000-000000000004");
        var idE = Guid.Parse("e0000000-0000-0000-0000-000000000005");
        var idInactive = Guid.Parse("f0000000-0000-0000-0000-000000000006");

        var lA = EquipmentListing.Create(idA, owner, typeId, brandId, "Listing A", null, "Model A", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lA.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lB = EquipmentListing.Create(idB, owner, typeId, brandId, "Listing B", null, "Model B", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lB.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lC = EquipmentListing.Create(idC, owner, typeId, brandId, "Listing C", null, "Model C", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lC.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lD = EquipmentListing.Create(idD, owner, typeId, brandId, "Listing D", null, "Model D", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lD.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lE = EquipmentListing.Create(idE, owner, typeId, brandId, "Listing E", null, "Model E", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        lE.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var lInactive = EquipmentListing.Create(idInactive, owner, typeId, brandId, "Inactive", null, "Model Inactive", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;

        db.EquipmentListings.AddRange(lA, lB, lC, lD, lE, lInactive);
        await db.SaveChangesAsync();

        var valkeyDb = _valkey.Multiplexer.GetDatabase();
        string globalKey = "rec:v1:global:popular-listings";
        await valkeyDb.SortedSetAddAsync(globalKey, idA.ToString("D"), 100);
        await valkeyDb.SortedSetAddAsync(globalKey, idB.ToString("D"), 50);
        await valkeyDb.SortedSetAddAsync(globalKey, idC.ToString("D"), 20);
        await valkeyDb.SortedSetAddAsync(globalKey, idD.ToString("D"), 5);

        // Request page 1 with pageSize 2 (cycle 5974968)
        var responseP1 = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, responseP1.StatusCode);
        var paged1 = await responseP1.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged1);
        Assert.Equal(5, paged1.TotalCount);
        Assert.DoesNotContain(idInactive, paged1.Items.Select(x => x.Id));

        // Expected vector for page 1: [idB, idA]
        Assert.Equal([idB, idA], paged1.Items.Select(x => x.Id));

        // Request page 2 with pageSize 2
        var responseP2 = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=2&pageSize=2");
        var paged2 = await responseP2.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged2);
        Assert.Equal([idE, idD], paged2.Items.Select(x => x.Id));

        // Request page 3 with pageSize 2
        var responseP3 = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=3&pageSize=2");
        var paged3 = await responseP3.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged3);
        Assert.Equal([idC], paged3.Items.Select(x => x.Id));

        // Combining all pages: exactly full set without duplicate or missing
        var combined = paged1.Items.Concat(paged2.Items).Concat(paged3.Items).Select(x => x.Id).ToList();
        Assert.Equal([idB, idA, idE, idD, idC], combined);

        // Same cycle/snapshot returns identical order on repeated call
        var repeatRes = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=1&pageSize=2");
        var repeatPaged = await repeatRes.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.Equal([idB, idA], repeatPaged!.Items.Select(x => x.Id));

        // Cycle 5974970 (10 minutes later): page 1 composition changes to [idC, idA]
        var (appLater, _) = CreateTestApp(now: FixedNow.AddMinutes(10));
        using var clientLater = appLater.CreateClient();
        var responseLater = await clientLater.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=1&pageSize=2");
        var pagedLater = await responseLater.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(pagedLater);
        Assert.Equal([idC, idA], pagedLater.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task AuthenticatedHuman_PublicListing_Recommended_UsesPersonalizedAndExcludesOwn()
    {
        await ResetValkeyAsync();
        await using var db = CreateDbContext();
        var (app, secFactory) = CreateTestApp();
        using var client = app.CreateClient();

        var myUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var otherUserId = Guid.Parse("99999999-8888-7777-6666-555555555555");
        await SeedWarehouseAsync(db, myUserId);
        await SeedWarehouseAsync(db, otherUserId);

        var brandId = Guid.NewGuid();
        var typeFav = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var typeOther = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var idFav1 = Guid.Parse("f1000000-0000-0000-0000-000000000001");
        var idFav2 = Guid.Parse("f2000000-0000-0000-0000-000000000002");
        var idNorm1 = Guid.Parse("c1000000-0000-0000-0000-000000000001");
        var idNorm2 = Guid.Parse("c2000000-0000-0000-0000-000000000002");

        // 1. My listing (should be excluded)
        var myListing = EquipmentListing.Create(
            Guid.NewGuid(), myUserId, typeFav, brandId, "My Listing", null, "My Model", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        myListing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        // 2. Other user's listings
        var fav1 = EquipmentListing.Create(idFav1, otherUserId, typeFav, brandId, "Fav 1", null, "M1", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        fav1.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var fav2 = EquipmentListing.Create(idFav2, otherUserId, typeFav, brandId, "Fav 2", null, "M2", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        fav2.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var norm1 = EquipmentListing.Create(idNorm1, otherUserId, typeOther, brandId, "Norm 1", null, "M3", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        norm1.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        var norm2 = EquipmentListing.Create(idNorm2, otherUserId, typeOther, brandId, "Norm 2", null, "M4", 1, "{}", 100_000m, null, null, "VND", FixedNow, 2024).Value;
        norm2.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);

        db.EquipmentListings.AddRange(myListing, fav1, fav2, norm1, norm2);
        await db.SaveChangesAsync();

        var valkeyDb = _valkey.Multiplexer.GetDatabase();
        string userKey = $"rec:v1:user:{myUserId:D}:equipment-types";
        await valkeyDb.SortedSetAddAsync(userKey, typeFav.ToString("D"), 50);

        string globalKey = "rec:v1:global:popular-listings";
        await valkeyDb.SortedSetAddAsync(globalKey, idFav1.ToString("D"), 100);
        await valkeyDb.SortedSetAddAsync(globalKey, idNorm1.ToString("D"), 100);

        string token = secFactory.CreateHumanToken(myUserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Cycle 5974968: page 1 (size 2) expected: [idFav2, idFav1]
        var responseP1 = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, responseP1.StatusCode);
        var paged1 = await responseP1.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged1);
        Assert.Equal(4, paged1.TotalCount);
        Assert.DoesNotContain(myListing.Id, paged1.Items.Select(x => x.Id)); // Owner exclusion verified
        Assert.Equal([idFav2, idFav1], paged1.Items.Select(x => x.Id));

        // Page 2 (size 2) expected: [idNorm1, idNorm2]
        var responseP2 = await client.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=2&pageSize=2");
        var paged2 = await responseP2.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(paged2);
        Assert.Equal([idNorm1, idNorm2], paged2.Items.Select(x => x.Id));

        // Combined: disjoint, complete
        var combined = paged1.Items.Concat(paged2.Items).Select(x => x.Id).ToList();
        Assert.Equal([idFav2, idFav1, idNorm1, idNorm2], combined);

        // Cycle 5974970 (10 minutes later): page 1 composition changes to [idNorm1, idFav1]
        var (appLater, secLater) = CreateTestApp(now: FixedNow.AddMinutes(10));
        using var clientLater = appLater.CreateClient();
        clientLater.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secLater.CreateHumanToken(myUserId));
        var responseLater = await clientLater.GetAsync($"api/v1/inventory/public/equipment-listings?brandId={brandId:D}&sort=recommended&page=1&pageSize=2");
        var pagedLater = await responseLater.Content.ReadFromJsonAsync<PagedResponse<PublicEquipmentListingResponse>>();
        Assert.NotNull(pagedLater);
        Assert.Equal([idFav1, idNorm1], pagedLater.Items.Select(x => x.Id));
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
