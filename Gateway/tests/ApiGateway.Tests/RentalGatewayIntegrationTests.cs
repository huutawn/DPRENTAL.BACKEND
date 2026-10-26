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

    [Fact]
    public async Task RentalOrderHubNegotiateWithCookieProxiesToDownstreamAndKeepsPath()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Must keep downstream hub path, NOT rewritten to /rental/...
        Assert.Equal("/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1", upstream.LastRequest?.Path);
        Assert.True(upstream.LastRequest?.Headers.ContainsKey("X-Api-Key"));
        Assert.Equal("dev-internal-api-key-dprental-2026", upstream.LastRequest?.Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task RentalOrderHubWebSocketWithCookieProxiesToDownstreamAndKeepsPath()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/rental/hubs/orders");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/rental/hubs/orders", upstream.LastRequest?.Path);
        Assert.True(upstream.LastRequest?.Headers.ContainsKey("X-Api-Key"));
        Assert.Equal("dev-internal-api-key-dprental-2026", upstream.LastRequest?.Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task RentalOrderHubAnonymousReturns401()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        // 1. Negotiate without token -> 401
        using HttpRequestMessage negotiateReq = new(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate");
        using HttpResponseMessage negotiateResp = await client.SendAsync(negotiateReq);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiateResp.StatusCode);

        // 2. WebSocket upgrade without token -> 401
        using HttpRequestMessage wsReq = new(HttpMethod.Get, "/api/v1/rental/hubs/orders");
        using HttpResponseMessage wsResp = await client.SendAsync(wsReq);
        Assert.Equal(HttpStatusCode.Unauthorized, wsResp.StatusCode);
    }

    [Fact]
    public async Task RentalOrderHubServiceTokenReturns403()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string serviceToken = CreateServiceToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));

        // 1. Negotiate with service token -> 403
        using HttpRequestMessage negotiateReq = new(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate");
        negotiateReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        using HttpResponseMessage negotiateResp = await client.SendAsync(negotiateReq);
        Assert.Equal(HttpStatusCode.Forbidden, negotiateResp.StatusCode);

        // 2. WebSocket with service token -> 403
        using HttpRequestMessage wsReq = new(HttpMethod.Get, "/api/v1/rental/hubs/orders");
        wsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        using HttpResponseMessage wsResp = await client.SendAsync(wsReq);
        Assert.Equal(HttpStatusCode.Forbidden, wsResp.StatusCode);
    }

    [Fact]
    public async Task RentalOrderHubStripsRefreshTokenAndForwardsAccessTokenCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate");
        request.Headers.Add("Cookie", $"accessToken={token}; refreshToken=sensitive-refresh-token-123; other=val");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(upstream.LastRequest?.Headers.ContainsKey("Cookie"));
        string forwardedCookie = upstream.LastRequest?.Headers["Cookie"] ?? string.Empty;
        Assert.Contains($"accessToken={token}", forwardedCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", forwardedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-refresh-token-123", forwardedCookie, StringComparison.Ordinal);
    }
}
