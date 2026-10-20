using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Security;

public sealed class InternalApiKeyMiddlewareTests
{
    private const string ValidKey = InventorySecurityTestFactory.DefaultInternalApiKey;

    [Fact]
    public async Task MissingApiKeyReturns401ProblemDetails()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        using var response = await client.GetAsync("/api/v1/inventory/public/equipment-listings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task InvalidApiKeyReturns401ProblemDetails()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

        using var response = await client.GetAsync("/api/v1/inventory/public/equipment-listings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task MultipleApiKeyHeadersReturns401ProblemDetails()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", [ValidKey, "duplicate-key"]);

        using var response = await client.GetAsync("/api/v1/inventory/public/equipment-listings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task HealthEndpointWithoutApiKeySucceeds()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BatchListingWithApiKeySucceedsWithoutServiceToken()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/inventory/equipment-listings/batch",
            new { equipmentListingIds = new[] { Guid.NewGuid() }, includeWarehouse = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
