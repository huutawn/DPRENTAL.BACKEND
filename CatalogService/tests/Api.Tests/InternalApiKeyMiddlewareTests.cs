using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class InternalApiKeyMiddlewareTests
{
    private const string ValidKey = CatalogWebApplicationFactory.DefaultInternalApiKey;

    [Fact]
    public async Task MissingApiKeyReturns401ProblemDetails()
    {
        using var factory = PublicCatalogEndpointTests.CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        using var response = await client.GetAsync("/api/v1/catalog/categories");

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
        using var factory = PublicCatalogEndpointTests.CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

        using var response = await client.GetAsync("/api/v1/catalog/categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task MultipleApiKeyHeadersReturns401ProblemDetails()
    {
        using var factory = PublicCatalogEndpointTests.CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", [ValidKey, "duplicate-key"]);

        using var response = await client.GetAsync("/api/v1/catalog/categories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task HealthEndpointWithoutApiKeySucceeds()
    {
        using var factory = PublicCatalogEndpointTests.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerEndpointWithoutApiKeySucceeds()
    {
        using var factory = PublicCatalogEndpointTests.CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
