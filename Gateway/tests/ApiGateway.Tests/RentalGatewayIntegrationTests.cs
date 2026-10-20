using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class RentalGatewayIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string HumanAudience = "dprental-clients";

    [Fact]
    public async Task RentalRouteValidBearerTokenProxiesAndForwardsToken()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/rental/me/calendar?month=2026-10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/rental/me/calendar?month=2026-10", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
    }

    [Fact]
    public async Task RentalRouteMissingBearerTokenReturns401()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/rental/me/calendar");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RentalOpenApiRouteAllowsAnonymous()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/rental/swagger/v1/swagger.json");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest?.Path);
    }
}
