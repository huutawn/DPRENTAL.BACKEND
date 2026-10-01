using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class RsaJwtAuthTests
{
    private const string ProtectedEndpointUrl = "/api/v1/catalog/test-protected";

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401_WithProblemDetails_AndWwwAuthenticate()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("WWW-Authenticate"));

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("Unauthorized", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidBearerToken_Returns200()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: "customer");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidCookieToken_Returns200()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: "customer");

        HttpRequestMessage request = new(HttpMethod.Get, ProtectedEndpointUrl);
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_BearerHeaderTakesPrecedenceOverCookie()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string validToken = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: "customer");

        string invalidToken = "invalid-cookie-token";

        HttpRequestMessage request = new(HttpMethod.Get, ProtectedEndpointUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
        request.Headers.Add("Cookie", $"accessToken={invalidToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithWrongIssuer_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            issuer: "https://wrong-issuer.example.com");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithWrongAudience_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            audience: "wrong-audience");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithDifferentKeySignature_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using RSA alienRsa = RSA.Create(2048);
        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            signingRsa: alienRsa);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithExpiredToken_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithFutureNotBefore_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            notBefore: DateTime.UtcNow.AddHours(2),
            expires: DateTime.UtcNow.AddHours(3));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithMissingOrUnknownKid_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            keyId: "unknown-kid");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithInvalidSubGuid_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: "not-a-guid",
            sid: Guid.NewGuid().ToString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_UserTokenWithoutSid_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: null);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_UserTokenWithInvalidSidGuid_Returns401()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: "not-a-valid-guid");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_ServiceTokenWithoutSid_Returns200()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: null,
            tokenUse: "svc");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PublicEndpoints_AndHealth_RemainAccessibleAnonymously()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage listRes = await client.GetAsync("/api/v1/catalog/equipments");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        using HttpResponseMessage liveRes = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveRes.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

                builder.ConfigureTestServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(RsaJwtAuthTests).Assembly);
                    services.RemoveAll<IEquipmentQueryRepository>();
                    services.AddScoped<IEquipmentQueryRepository, FakeEquipmentQueryRepository>();
                });
            });

    private sealed class FakeEquipmentQueryRepository : IEquipmentQueryRepository
    {
        public Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(GetEquipmentsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<EquipmentListItemDto>([], 1, 20, 0));

        public Task<EquipmentDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<EquipmentDetailDto?>(null);
    }
}

/// <summary>Endpoint chỉ có trong test để kiểm thử hành vi bảo vệ xác thực mặc định.</summary>
[ApiController]
[Route("test-protected")]
public sealed class TestProtectedController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { message = "authorized" });
}
