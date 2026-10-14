using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests
{
    [Fact]
    public async Task HttpSearch_PreservesWirePayloadAndBackendHeader_WithStrictCredentialErrors()
    {
        var listing = NewListing("Máy ảnh HTTP");
        _db.EquipmentListings.Add(listing);
        await _db.SaveChangesAsync();
        await _store.UpsertJobAsync("EquipmentListing", listing.Id, "Upsert", 1, false, DateTimeOffset.UtcNow);
        await using var provider = BuildSearchProvider();
        Assert.Equal(1, await CreateWorker(provider).ProcessBatchAsync());
        await using var fixture = new InventorySecurityTestFactory(_postgres.ConnectionString);
        await using var app = fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IInventorySearchIndexService>(sp => new MeilisearchInventoryIndexService(
                _client, sp.GetRequiredService<ISearchProjectionStore>(), Options.Create(_options),
                NullLogger<MeilisearchInventoryIndexService>.Instance))));
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/v1/inventory/public/equipment-listings?q=may%20anh");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("meilisearch", Assert.Single(response.Headers.GetValues("X-Search-Backend")));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(listing.Id, json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.False(json.RootElement.TryGetProperty("value", out _));
        Assert.False(json.RootElement.TryGetProperty("searchBackend", out _));
        using var noKeyword = await client.GetAsync("/api/v1/inventory/public/equipment-listings");
        Assert.Equal("postgres", Assert.Single(noKeyword.Headers.GetValues("X-Search-Backend")));
        var generation = (await _store.GetGenerationAsync())!;
        generation.MarkUnavailable("SEARCH_CREDENTIALS_INVALID", DateTimeOffset.UtcNow);
        await _store.SaveGenerationAsync(generation);
        using var denied = await client.GetAsync("/api/v1/inventory/public/equipment-listings?q=camera");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("SEARCH_CREDENTIALS_INVALID", problem.RootElement.GetProperty("title").GetString());
    }
}
