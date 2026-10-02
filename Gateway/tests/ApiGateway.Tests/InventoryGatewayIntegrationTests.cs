using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class InventoryGatewayIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string HumanAudience = "dprental-clients";
    private const string ServiceAudience = "inventory-api";

    [Fact]
    public async Task InventoryRouteValidHumanBearerTokenProxiesAndStripsCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/assets/123");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Cookie", "session=12345; other=cookie");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/assets/123", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task InventoryRouteValidServiceTokenProxiesSuccessfully()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateServiceToken(signingKey, "key-1", Issuer, ServiceAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/inventory/allocations/hold");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/allocations/hold", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
    }

    [Fact]
    public async Task NonInventoryRouteServiceTokenWithInventoryAudienceIsRejectedWith401()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateServiceToken(signingKey, "key-1", Issuer, ServiceAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/media/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        // Must reject because inventory-api audience is isolated from non-inventory routes!
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InventoryRouteAccessTokenCookieConvertsToBearerAndStripsCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/calendar");
        request.Headers.Add("Cookie", $"accessToken={token}; theme=dark");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task InventoryRouteSpoofedHeadersAreStrippedBeforeDownstream()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/assets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(GatewayHeaderConstants.UserId, "forged-user-id");
        request.Headers.Add(GatewayHeaderConstants.Role, "forged-role");
        request.Headers.Add(GatewayHeaderConstants.OrganizationId, "forged-org-id");
        request.Headers.Add(GatewayHeaderConstants.PartyId, "forged-party-id");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.UserId));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.Role));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.OrganizationId));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.PartyId));
    }

    [Fact]
    public async Task InventoryOpenApiRouteAllowsAnonymous()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/inventory/swagger/v1/swagger.json");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest?.Path);
    }

}
