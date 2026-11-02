using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Api.Caching;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class RecommendationHttpEndToEndIntegrationTests
{
    [Fact]
    public async Task BehaviorEvents_ArePersistedOnCacheMissAndHit_AndRespectPrefetch()
    {
        await ResetValkeyAsync();
        await using var db = CreateDbContext();
        await db.OutboxMessages.ExecuteDeleteAsync();
        var listing = await SeedBehaviorListingAsync(db);
        await using var security = new InventorySecurityTestFactory(_postgres.ConnectionString);
        await using var app = security.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInventoryReadCache>();
            services.AddSingleton<IInventoryReadCache>(new ValkeyReadCache(
                Options.Create(new InventoryCacheOptions { Enabled = true, EnvironmentName = "BehaviorTests", DynamicTtlSeconds = 120 }),
                _valkey.Multiplexer));
        }));
        using var client = app.CreateClient();
        string detail = $"/api/v1/inventory/public/equipment-listings/{listing.Id:D}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(detail)).StatusCode);
        var cache = app.Services.GetRequiredService<IInventoryReadCache>();
        string today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var cached = await cache.GetAsync<Dprental.InventoryService.Application.DTOs.Listings.PublicEquipmentListingResponse>(
            $"listing:{listing.Id:D}", "public-detail-v2", today);
        Assert.True(cached.IsHit);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(detail)).StatusCode);
        var views = await db.OutboxMessages.AsNoTracking().Where(m => m.Topic == KafkaTopics.InventoryBehaviorEventsV1).ToListAsync();
        Assert.Equal(2, views.Count);
        Assert.All(views, m =>
        {
            using var payload = JsonDocument.Parse(m.Payload);
            Assert.Equal("ListingViewed", payload.RootElement.GetProperty("messageType").GetString());
            Assert.True(RecommendationContractResources.Evaluate("listing-viewed.schema.json", payload.RootElement).IsValid);
        });
        using var prefetch = new HttpRequestMessage(HttpMethod.Get, detail);
        prefetch.Headers.Add("Purpose", "navigate");
        prefetch.Headers.Add("Sec-Purpose", "prefetch;prerender");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(prefetch)).StatusCode);
        Assert.Equal(2, await db.OutboxMessages.CountAsync(m => m.Topic == KafkaTopics.InventoryBehaviorEventsV1));

        string search = $"/api/v1/inventory/public/equipment-listings?equipmentTypeId={listing.EquipmentTypeId:D}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(search)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(search)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/inventory/public/equipment-listings?brandId={Guid.NewGuid():D}")).StatusCode);
        var searches = (await db.OutboxMessages.AsNoTracking().Where(m => m.Topic == KafkaTopics.InventoryBehaviorEventsV1).ToListAsync())
            .Where(m => ReadMessageType(m.Payload) == "SearchPerformed").ToList();
        Assert.Equal(3, searches.Count);
        Assert.Contains(searches, m => ReadResultCount(m.Payload) == 0);
        Assert.All(searches, m =>
        {
            using var payload = JsonDocument.Parse(m.Payload);
            Assert.True(RecommendationContractResources.Evaluate("search-performed.schema.json", payload.RootElement).IsValid);
        });
    }

    [Fact]
    public async Task LockedOutbox_TimesOutWithoutFailingCachedDetail_OrPersistingLateMessage()
    {
        await ResetValkeyAsync();
        await using var db = CreateDbContext();
        await db.OutboxMessages.ExecuteDeleteAsync();
        var listing = await SeedBehaviorListingAsync(db);
        await using var security = new InventorySecurityTestFactory(_postgres.ConnectionString);
        await using var app = security.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInventoryReadCache>();
            services.AddSingleton<IInventoryReadCache>(new ValkeyReadCache(
                Options.Create(new InventoryCacheOptions { Enabled = true, EnvironmentName = "BehaviorTimeoutTests", DynamicTtlSeconds = 120 }),
                _valkey.Multiplexer));
        }));
        using var client = app.CreateClient();
        string detail = $"/api/v1/inventory/public/equipment-listings/{listing.Id:D}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(detail)).StatusCode);
        int before = await db.OutboxMessages.CountAsync();
        await using var blocker = new NpgsqlConnection(_postgres.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("LOCK TABLE inventory.outbox_messages IN ACCESS EXCLUSIVE MODE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(detail).WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        await transaction.RollbackAsync();
        Assert.Equal(before, await db.OutboxMessages.CountAsync());
    }

    private static string? ReadMessageType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("messageType").GetString();
    }

    private static int ReadResultCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("payload").GetProperty("resultCount").GetInt32();
    }

    private static async Task<EquipmentListing> SeedBehaviorListingAsync(Dprental.InventoryService.Domain.Repositories.InventoryServiceDbContext db)
    {
        var owner = Guid.NewGuid();
        await SeedWarehouseAsync(db, owner);
        var listing = EquipmentListing.Create(Guid.NewGuid(), owner, Guid.NewGuid(), Guid.NewGuid(),
            "Behavior fixture", null, "Behavior model", 1, "{}", 100_000m, null, null, "VND", DateTimeOffset.UtcNow, 2024).Value;
        listing.UpdateStatus(Dprental.InventoryService.Common.Enums.EquipmentListingStatus.Active, true, DateTimeOffset.UtcNow);
        db.EquipmentListings.Add(listing);
        await db.SaveChangesAsync();
        return listing;
    }
}
