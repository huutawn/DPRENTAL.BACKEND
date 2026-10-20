using System.Net;
using System.Net.Http.Json;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Security;

public sealed class InternalApiKeyMiddlewareTests
{
    private const string ValidKey = PaymentWebApplicationFactory.DefaultInternalApiKey;

    [Fact]
    public async Task MissingApiKeyReturns401ProblemDetails()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("InternalAuthentication:ApiKey", ValidKey);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/payment/history");

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
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("InternalAuthentication:ApiKey", ValidKey);
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");

        using var response = await client.GetAsync("/api/v1/payment/history");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task MultipleApiKeyHeadersReturns401ProblemDetails()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("InternalAuthentication:ApiKey", ValidKey);
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", [ValidKey, "duplicate-key"]);

        using var response = await client.GetAsync("/api/v1/payment/history");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(401, problem.Status);
    }

    [Fact]
    public async Task HealthEndpointWithoutApiKeySucceeds()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("InternalAuthentication:ApiKey", ValidKey);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
