using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class InternalApiKeyMiddlewareTests
{
    private const string ValidKey = AuthWebApplicationFactory.DefaultInternalApiKey;

    [Fact]
    public async Task MissingApiKeyReturns401ProblemDetails()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/auth/users/me");

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
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

        using var response = await client.GetAsync("/api/v1/auth/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task MultipleApiKeyHeadersReturns401ProblemDetails()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", [ValidKey, "duplicate-key"]);

        using var response = await client.GetAsync("/api/v1/auth/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task PublicLoginEndpointWithoutApiKeyReturns401ProblemDetails()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "test@example.com", password = "Password123!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task HealthEndpointWithoutApiKeySucceeds()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task JwksEndpointWithoutApiKeySucceeds()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceTokenEndpointIsRemoved()
    {
        using var factory = new AuthWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/auth/service-token", new { clientId = "c", clientSecret = "s", audience = "a" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
