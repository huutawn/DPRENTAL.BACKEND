using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class JwksAuthTests
{
    private const string ProtectedEndpointUrl = "/api/v1/catalog/test-protected";

    [Fact]
    public async Task Manager_CachesKeys_AndDoesNotCallAuthServicePerRequest()
    {
        TestJwks.StubJwksHandler handler = new();
        using JwksConfigurationManager manager = CreateManager(handler, new MutableTimeProvider());

        OpenIdConnectConfiguration first = await manager.GetConfigurationAsync(CancellationToken.None);
        OpenIdConnectConfiguration second = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(TestJwtHelper.DefaultKeyId, Assert.Single(first.SigningKeys).KeyId);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Manager_RefreshesAfterCacheDuration_AndHonoursShorterMaxAge()
    {
        TestJwks.StubJwksHandler handler = new() { MaxAge = TimeSpan.FromMinutes(1) };
        MutableTimeProvider time = new();
        using JwksConfigurationManager manager = CreateManager(handler, time);

        await manager.GetConfigurationAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, handler.RequestCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Manager_UnknownKid_RefreshesOnce_AndAcceptsRotatedKey()
    {
        TestJwks.StubJwksHandler handler = new();
        MutableTimeProvider time = new();
        using JwksConfigurationManager manager = CreateManager(handler, time);
        using RSA rotatedRsa = RSA.Create(2048);

        Assert.True(await manager.EnsureSigningKeyAsync(TestJwtHelper.DefaultKeyId, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);

        // kid lạ: tải lại đúng một lần, các lần sau trong khoảng giãn cách không dội request sang AuthService.
        Assert.False(await manager.EnsureSigningKeyAsync("unknown-kid", CancellationToken.None));
        Assert.False(await manager.EnsureSigningKeyAsync("unknown-kid", CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);

        handler.Document = TestJwks.CreateDocument((TestJwtHelper.DefaultKeyId, TestJwtHelper.RsaInstance), ("key-rotated", rotatedRsa));
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(await manager.EnsureSigningKeyAsync("key-rotated", CancellationToken.None));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Manager_AuthServiceOutage_UsesCacheOnlyWithinMaxStale()
    {
        TestJwks.StubJwksHandler handler = new();
        MutableTimeProvider time = new();
        using JwksConfigurationManager manager = CreateManager(handler, time);

        OpenIdConnectConfiguration cached = await manager.GetConfigurationAsync(CancellationToken.None);
        handler.Available = false;

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Same(cached, await manager.GetConfigurationAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Manager_RejectsJwks_WithoutAcceptableSigningKey()
    {
        using RSA weakRsa = RSA.Create(1024);
        TestJwks.StubJwksHandler handler = new() { Document = TestJwks.CreateDocument(("k-weak", weakRsa)) };
        using JwksConfigurationManager manager = CreateManager(handler, new MutableTimeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Api_ValidatesManyRequests_WithSingleJwksFetch()
    {
        TestJwks.StubJwksHandler handler = new();
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken());

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Api_AcceptsTokenSignedByKeyPublishedAfterStartup()
    {
        TestJwks.StubJwksHandler handler = new();
        using WebApplicationFactory<Program> factory = CreateFactory(handler);
        using HttpClient client = factory.CreateClient();
        using RSA rotatedRsa = RSA.Create(2048);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken());
        using HttpResponseMessage warmUp = await client.GetAsync(ProtectedEndpointUrl);
        Assert.Equal(HttpStatusCode.OK, warmUp.StatusCode);

        handler.Document = TestJwks.CreateDocument((TestJwtHelper.DefaultKeyId, TestJwtHelper.RsaInstance), ("key-rotated", rotatedRsa));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString(), keyId: "key-rotated", signingRsa: rotatedRsa));

        using HttpResponseMessage response = await client.GetAsync(ProtectedEndpointUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_AuthServiceUnavailable_Returns401ForProtected_AndKeepsPublicEndpoints()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(new TestJwks.StubJwksHandler { Available = false });
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken());

        using HttpResponseMessage protectedResponse = await client.GetAsync(ProtectedEndpointUrl);
        using HttpResponseMessage publicResponse = await client.GetAsync("/api/v1/catalog/equipment-types");

        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
    }

    private static string ValidToken() =>
        TestJwtHelper.GenerateToken(sub: Guid.NewGuid().ToString(), sid: Guid.NewGuid().ToString());

    private static JwksConfigurationManager CreateManager(HttpMessageHandler handler, TimeProvider timeProvider) =>
        new(new HttpClient(handler), new JwtOptions
        {
            Issuer = TestJwtHelper.DefaultIssuer,
            Audience = TestJwtHelper.DefaultAudience,
            JwksUri = TestJwks.Uri
        }, timeProvider);

    private static WebApplicationFactory<Program> CreateFactory(HttpMessageHandler jwksHandler) =>
        new CatalogWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks(jwksHandler);

                builder.ConfigureTestServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(JwksAuthTests).Assembly);
                    services.RemoveAll<IEquipmentTypeRepository>();
                    services.AddScoped<IEquipmentTypeRepository, RsaJwtAuthTests.FakeEquipmentTypeRepository>();
                });
            });

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
