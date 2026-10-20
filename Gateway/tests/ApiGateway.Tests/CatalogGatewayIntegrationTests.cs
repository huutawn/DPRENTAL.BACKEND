using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Dprental.Gateway.Security;
using Xunit;
using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;

namespace Dprental.Gateway.Tests;

public sealed class CatalogGatewayIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";

    [Theory]
    [InlineData("/api/v1/catalog/categories")]
    [InlineData("/api/v1/catalog/categories/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/catalog/equipment-types?keyword=crane&categoryId=11111111-1111-1111-1111-111111111111&page=2&pageSize=10&sort=name_asc")]
    [InlineData("/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/catalog/brands")]
    [InlineData("/api/v1/catalog/brands?keyword=crane&page=2&pageSize=10&sort=name_asc")]
    [InlineData("/api/v1/catalog/brands/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/schema")]
    [InlineData("/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/schemas/1")]
    [InlineData("/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/spec-schemas/1")]
    public async Task PublicReadsPreservePathAndQueryWithoutAuthentication(string path)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, upstream.LastRequest?.Path);
    }

    [Theory]
    [InlineData("POST", "/api/v1/catalog/equipment-types")]
    [InlineData("PATCH", "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/publish")]
    [InlineData("POST", "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/disable")]
    [InlineData("POST", "/api/v1/catalog/categories")]
    [InlineData("PATCH", "/api/v1/catalog/categories/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/v1/catalog/equipment-types/admin/export")]
    [InlineData("POST", "/api/v1/catalog/brands")]
    [InlineData("PATCH", "/api/v1/catalog/brands/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/v1/catalog/brands/11111111-1111-1111-1111-111111111111/disable")]
    [InlineData("POST", "/api/v1/catalog/brands/11111111-1111-1111-1111-111111111111/enable")]
    [InlineData("GET", "/api/v1/catalog/admin/brands")]
    [InlineData("GET", "/api/v1/catalog/admin/brands/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/v1/catalog/brands/11111111-1111-1111-1111-111111111111/enable")]
    [InlineData("POST", "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/spec-schemas/1/publish")]
    [InlineData("POST", "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/specifications/validate")]
    public async Task ProtectedRoutesRequireBearerAndRejectWrongAudience(string method, string path)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage anonymous = new(new HttpMethod(method), path);
        using HttpResponseMessage anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using HttpRequestMessage invalid = new(new HttpMethod(method), path);
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateServiceToken(key, "key-1", Issuer, "inventory-api", Guid.NewGuid().ToString("D")));
        using HttpResponseMessage invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Null(upstream.LastRequest);
    }

    [Fact]
    public async Task PublicSchemaBatchPreservesBodyWithoutAuthentication()
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        const string path = "/api/v1/catalog/equipment-types/11111111-1111-1111-1111-111111111111/schemas/batch";
        const string body = "{\"versions\":[1,2]}";
        using StringContent content = new(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, upstream.LastRequest?.Path);
        Assert.Equal(body, upstream.LastRequest?.Body);
        Assert.True(string.IsNullOrEmpty(upstream.LastRequest?.Authorization));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task PublicBrandErrorsArePreserved(int statusCode)
    {
        using RSA key = RSA.Create(2048);
        const string body = "{\"title\":\"Brand.TestError\"}";
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"))
        {
            ResponseStatusCode = statusCode,
            ResponseBody = body,
            ResponseContentType = "application/problem+json"
        };
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/brands?unknown=value");

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesForwardBearerAndBodyWithoutCookiesOrIdentityHeaders(bool useCookie)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateToken(key, "key-1", Issuer, Audience, Guid.NewGuid().ToString("D"), "Admin");
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/equipment-types")
        {
            Content = new StringContent("{\"code\":\"CRANE\",\"name\":\"Crane\"}", Encoding.UTF8, "application/json")
        };
        if (useCookie)
        {
            request.Headers.Add("Cookie", $"accessToken={token}; session=private");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Cookie", "session=private");
        }
        request.Headers.Add(GatewayHeaderConstants.UserId, "spoofed-user");
        request.Headers.Add(GatewayHeaderConstants.Role, "spoofed-role");
        request.Headers.Add("X-Api-Key", "spoofed-key");
        string correlationId = Guid.NewGuid().ToString("D");
        request.Headers.Add(GatewayHeaderConstants.CorrelationId, correlationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.Equal("{\"code\":\"CRANE\",\"name\":\"Crane\"}", upstream.LastRequest?.Body);
        Assert.NotNull(upstream.LastRequest);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Cookie"));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.UserId));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.Role));
        Assert.True(upstream.LastRequest.Headers.ContainsKey("X-Api-Key"));
        Assert.NotEqual("spoofed-key", upstream.LastRequest.Headers["X-Api-Key"]);
        Assert.Equal("dev-internal-api-key-dprental-2026", upstream.LastRequest.Headers["X-Api-Key"]);
        Assert.Equal(correlationId, upstream.LastRequest.Headers[GatewayHeaderConstants.CorrelationId]);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task DownstreamProblemDetailsArePreserved(int statusCode)
    {
        using RSA key = RSA.Create(2048);
        const string body = "{\"title\":\"EquipmentType.TestError\",\"detail\":\"Catalog validation\"}";
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"))
        {
            ResponseStatusCode = statusCode,
            ResponseBody = body,
            ResponseContentType = "application/problem+json"
        };
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateToken(key, "key-1", Issuer, Audience, Guid.NewGuid().ToString("D")));

        using HttpResponseMessage response = await client.PostAsync("/api/v1/catalog/equipment-types", null);

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CatalogOpenApiIsProxiedToTheServiceDocument()
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/catalog/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest?.Path);
    }
}
