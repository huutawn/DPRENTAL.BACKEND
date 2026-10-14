using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData("/api/v1/inventory/public/equipment-listings")]
    [InlineData("/api/v1/inventory/public/equipment-listings?q=forklift&equipmentTypeId=11111111-1111-1111-1111-111111111111&page=2&pageSize=10&sort=model_asc")]
    [InlineData("/api/v1/inventory/public/equipment-listings/11111111-1111-1111-1111-111111111111")]
    public async Task PublicInventoryReadsPreservePathAndQueryWithoutAuthentication(string path)
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, upstream.LastRequest?.Path);
        Assert.True(string.IsNullOrEmpty(upstream.LastRequest?.Authorization));
    }

    [Fact]
    public async Task PublicInventoryRoutePreservesErrorResponses()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"))
        {
            ResponseStatusCode = (int)HttpStatusCode.NotFound,
            ResponseBody = "{\"title\":\"ASSET_NOT_FOUND\",\"status\":404}",
            ResponseContentType = "application/problem+json"
        };
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/inventory/public/equipment-listings/00000000-0000-0000-0000-000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ASSET_NOT_FOUND", body);
    }

    [Theory]
    [InlineData("POST", "/api/v1/inventory/public/equipment-listings")]
    [InlineData("PUT", "/api/v1/inventory/public/equipment-listings/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/v1/inventory/public/equipment-listings/11111111-1111-1111-1111-111111111111")]
    public async Task NonGetPublicInventoryRoutesFallThroughToAuthenticatedCatchAll(string method, string path)
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(new HttpMethod(method), path);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/inventory/assets")]
    [InlineData("POST", "/api/v1/inventory/allocations/hold")]
    public async Task ProtectedInventoryRoutesWithoutAuthenticationAreRejectedWith401(string method, string path)
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(new HttpMethod(method), path);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void GatewayConfigurationBaseAndDevelopmentRoutesPointToExpectedDownstreams()
    {
        var ocelotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ocelot"));
        var configBuilder = new ConfigurationBuilder();
        configBuilder.SetBasePath(ocelotDir);
        configBuilder.AddJsonFile("appsettings.json", optional: false);
        configBuilder.AddJsonFile("appsettings.Development.json", optional: true);
        var config = configBuilder.Build();

        var routes = config.GetSection("Routes").GetChildren().ToList();
        var routeMap = routes.ToDictionary(
            r => r["Key"] ?? throw new InvalidOperationException("Route missing Key"),
            r => (
                Host: r.GetSection("DownstreamHostAndPorts:0:Host").Value,
                Port: r.GetSection("DownstreamHostAndPorts:0:Port").Value
            ));

        // Public Inventory routes point to Inventory downstream in Development
        Assert.Equal("localhost", routeMap["inventory-service-public-equipment-listings"].Host);
        Assert.Equal("43622", routeMap["inventory-service-public-equipment-listings"].Port);

        Assert.Equal("localhost", routeMap["inventory-service-public-equipment-listing-detail"].Host);
        Assert.Equal("43622", routeMap["inventory-service-public-equipment-listing-detail"].Port);


        Assert.Equal("localhost", routeMap["inventory-service-public-equipment-listings-statistics"].Host);
        Assert.Equal("43622", routeMap["inventory-service-public-equipment-listings-statistics"].Port);
        // Catalog routes point to Catalog downstream in Development
        Assert.Equal("localhost", routeMap["catalog-service-public-categories"].Host);
        Assert.Equal("47297", routeMap["catalog-service-public-categories"].Port);

        Assert.Equal("localhost", routeMap["catalog-service-public-equipment-types"].Host);
        Assert.Equal("47297", routeMap["catalog-service-public-equipment-types"].Port);

        Assert.Equal("localhost", routeMap["catalog-service"].Host);
        Assert.Equal("47297", routeMap["catalog-service"].Port);

        Assert.Equal("localhost", routeMap["catalog-service-openapi"].Host);
        Assert.Equal("47297", routeMap["catalog-service-openapi"].Port);
    }

    [Fact]
    public void GatewayConfigurationProductionBaseRoutesPointToExpectedServiceContainers()
    {
        var ocelotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ocelot"));
        var configBuilder = new ConfigurationBuilder();
        configBuilder.SetBasePath(ocelotDir);
        configBuilder.AddJsonFile("appsettings.json", optional: false);
        var config = configBuilder.Build();

        var routes = config.GetSection("Routes").GetChildren().ToList();
        var routeMap = routes.ToDictionary(
            r => r["Key"] ?? throw new InvalidOperationException("Route missing Key"),
            r => (
                Host: r.GetSection("DownstreamHostAndPorts:0:Host").Value,
                Port: r.GetSection("DownstreamHostAndPorts:0:Port").Value
            ));

        Assert.Equal("inventory-service", routeMap["inventory-service-public-equipment-listings"].Host);
        Assert.Equal("8080", routeMap["inventory-service-public-equipment-listings"].Port);

        Assert.Equal("inventory-service", routeMap["inventory-service-public-equipment-listing-detail"].Host);
        Assert.Equal("8080", routeMap["inventory-service-public-equipment-listing-detail"].Port);

        Assert.Equal("inventory-service", routeMap["inventory-service-public-equipment-listings-statistics"].Host);
        Assert.Equal("8080", routeMap["inventory-service-public-equipment-listings-statistics"].Port);

        Assert.Equal("catalog-service", routeMap["catalog-service-public-categories"].Host);
        Assert.Equal("catalog-service", routeMap["catalog-service"].Host);
    }

    [Fact]
    public async Task PublicInventoryStatisticsRouteProxiesAnonymously()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"))
        {
            ResponseBody = "{\"byEquipmentType\":{},\"byBrand\":{}}",
            ResponseContentType = "application/json"
        };
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/inventory/public/equipment-listings/statistics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/public/equipment-listings/statistics", upstream.LastRequest?.Path);
    }

    [Fact]
    public async Task PrivateInventoryStatisticsRouteProxiesWithBearerToken()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"))
        {
            ResponseBody = "{\"byEquipmentType\":{},\"byBrand\":{}}",
            ResponseContentType = "application/json"
        };
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/equipment-listings/statistics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/equipment-listings/statistics", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
    }
}
