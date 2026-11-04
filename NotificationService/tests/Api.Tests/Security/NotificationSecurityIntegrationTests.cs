using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Security;

public sealed class NotificationSecurityIntegrationTests : IClassFixture<NotificationSecurityIntegrationTests.SecurityTestFixture>
{
    private readonly SecurityTestFixture _fixture;

    public NotificationSecurityIntegrationTests(SecurityTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RestEndpoint_ValidHumanJwt_Returns200Ok()
    {
        using var client = _fixture.CreateClient();
        var userId = Guid.NewGuid();
        var token = _fixture.CreateToken(userId);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_ValidHumanJwt_ViaHeader_Returns200Ok()
    {
        using var client = _fixture.CreateClient();
        var userId = Guid.NewGuid();
        var token = _fixture.CreateToken(userId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("connectionId", out _) || doc.RootElement.TryGetProperty("connectionToken", out _));
        Assert.True(doc.RootElement.TryGetProperty("negotiateVersion", out _));
    }

    [Fact]
    public async Task HubNegotiate_ValidHumanJwt_ViaCookie_Returns200Ok()
    {
        using var client = _fixture.CreateClient();
        var userId = Guid.NewGuid();
        var token = _fixture.CreateToken(userId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthHeaderPrecedence_WhenAuthHeaderInvalidAndCookieValid_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        var validToken = _fixture.CreateToken(Guid.NewGuid());
        var invalidToken = "invalid-malformed-token";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);
        request.Headers.Add("Cookie", $"accessToken={validToken}");

        using var response = await client.SendAsync(request);
        // Authorization header has precedence: must not fall back to cookie!
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthHeaderPrecedence_WhenAuthHeaderValidAndCookieInvalid_Returns200Ok()
    {
        using var client = _fixture.CreateClient();
        var validToken = _fixture.CreateToken(Guid.NewGuid());
        var invalidToken = "invalid-malformed-token";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
        request.Headers.Add("Cookie", $"accessToken={invalidToken}");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequest_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();

        using var restResponse = await client.GetAsync("/api/v1/notifications");
        Assert.Equal(HttpStatusCode.Unauthorized, restResponse.StatusCode);

        using var hubRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        using var hubResponse = await client.SendAsync(hubRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResponse.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        var expiredToken = _fixture.CreateToken(Guid.NewGuid(), expired: true);

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Unauthorized, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResp.StatusCode);
    }

    [Fact]
    public async Task WrongIssuer_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        var token = _fixture.CreateToken(Guid.NewGuid(), issuer: "https://wrong-issuer.com");

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Unauthorized, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResp.StatusCode);
    }

    [Fact]
    public async Task WrongAudience_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        var token = _fixture.CreateToken(Guid.NewGuid(), audience: "wrong-audience");

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Unauthorized, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResp.StatusCode);
    }

    [Fact]
    public async Task WrongSignature_Returns401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        using var unknownRsa = RSA.Create(2048);
        var token = _fixture.CreateToken(Guid.NewGuid(), signingRsa: unknownRsa, keyId: "untrusted-kid");

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Unauthorized, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResp.StatusCode);
    }

    [Fact]
    public async Task ServiceJwt_Returns403Forbidden()
    {
        using var client = _fixture.CreateClient();
        var serviceToken = _fixture.CreateToken(Guid.NewGuid(), serviceToken: true);

        // REST endpoint -> 403
        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Forbidden, restResp.StatusCode);

        // Hub negotiate -> 403
        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Forbidden, hubResp.StatusCode);
    }

    [Fact]
    public async Task NonGuidSub_Returns403Forbidden()
    {
        using var client = _fixture.CreateClient();
        var badSubToken = _fixture.CreateToken(subOverride: "not-a-valid-guid");

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", badSubToken);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Forbidden, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", badSubToken);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Forbidden, hubResp.StatusCode);
    }

    [Fact]
    public async Task MissingSub_Returns403Forbidden()
    {
        using var client = _fixture.CreateClient();
        var noSubToken = _fixture.CreateToken(subOverride: "");

        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", noSubToken);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.Forbidden, restResp.StatusCode);

        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", noSubToken);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Forbidden, hubResp.StatusCode);
    }

    [Fact]
    public async Task QueryStringToken_StrictlyRejectedWith401Unauthorized()
    {
        using var client = _fixture.CreateClient();
        var validToken = _fixture.CreateToken(Guid.NewGuid());

        // REST endpoint with query string access_token
        using var restResp = await client.GetAsync($"/api/v1/notifications?access_token={validToken}");
        Assert.Equal(HttpStatusCode.Unauthorized, restResp.StatusCode);

        // Hub negotiate with query string access_token
        using var hubReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1&access_token={validToken}");
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.Unauthorized, hubResp.StatusCode);
    }

    [Fact]
    public async Task KeyRotation_NewKeyRecognizedAfterRefresh()
    {
        using var client = _fixture.CreateClient();
        // 1. Advance time past the 30-second refresh throttle and enable rotated key in JWKS
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(35));
        _fixture.IncludeRotatedKeyInJwks = true;

        // 2. Request refresh to recognize the rotated key
        var configManager = _fixture.Services.GetRequiredService<NotificationJwksConfigurationManager>();
        configManager.RequestRefresh();

        // 3. Create token signed by rotated key
        var rotatedToken = _fixture.CreateToken(
            userId: Guid.NewGuid(),
            signingRsa: _fixture.RotatedRsa,
            keyId: _fixture.RotatedKeyId);
        // 3. Send request to REST endpoint: should trigger JWKS refresh and succeed!
        using var restReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        restReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedToken);
        using var restResp = await client.SendAsync(restReq);
        Assert.Equal(HttpStatusCode.OK, restResp.StatusCode);

        // 4. Send request to Hub negotiate: should also succeed with rotated key!
        using var hubReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/hubs/inbox/negotiate?negotiateVersion=1");
        hubReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedToken);
        using var hubResp = await client.SendAsync(hubReq);
        Assert.Equal(HttpStatusCode.OK, hubResp.StatusCode);
    }

    public sealed class SecurityTestFixture : WebApplicationFactory<Program>
    {
        public const string Issuer = "https://auth.dprental.vn";
        public const string Audience = "dprental-platform";
        public const string JwksUri = "http://localhost/test-jwks";

        public FakeTimeProvider TimeProvider { get; } = new(DateTimeOffset.UtcNow);
        public RSA PrimaryRsa { get; } = RSA.Create(2048);
        public string PrimaryKeyId { get; } = "test-key-primary";

        public RSA RotatedRsa { get; } = RSA.Create(2048);
        public string RotatedKeyId { get; } = "test-key-rotated";

        public bool IncludeRotatedKeyInJwks { get; set; }
        public string GetJwksJson()
        {
            var keys = new List<object>();

            var primaryKey = new RsaSecurityKey(PrimaryRsa) { KeyId = PrimaryKeyId };
            var primaryJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(primaryKey);
            primaryJwk.Alg = SecurityAlgorithms.RsaSha256;
            primaryJwk.Use = "sig";
            keys.Add(primaryJwk);

            if (IncludeRotatedKeyInJwks)
            {
                var rotatedKey = new RsaSecurityKey(RotatedRsa) { KeyId = RotatedKeyId };
                var rotatedJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(rotatedKey);
                rotatedJwk.Alg = SecurityAlgorithms.RsaSha256;
                rotatedJwk.Use = "sig";
                keys.Add(rotatedJwk);
            }

            var jwks = new { keys };
            return JsonSerializer.Serialize(jwks);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.UseSetting("Kafka:BootstrapServers", "localhost:9092");
            builder.UseSetting("Kafka:Topics", "rental.events.v1,payment.analytics.v1");
            builder.UseSetting("Services:AuthService:BaseUrl", "http://auth-service:8080");
            builder.UseSetting("InternalAuthentication:ApiKey", "test-internal-api-key");
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);
            builder.UseSetting("Jwt:JwksUri", JwksUri);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<NotificationServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<NotificationServiceDbContext>>();
                services.AddDbContext<NotificationServiceDbContext>(options =>
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

                services.RemoveAll<IHostedService>();
                services.AddSingleton<TimeProvider>(TimeProvider);

                services.AddHttpClient(SignalRAuthenticationExtensions.JwksHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new TestJwksHttpMessageHandler(this));
            });
        }

        public string CreateToken(
            Guid? userId = null,
            string? issuer = null,
            string? audience = null,
            bool serviceToken = false,
            bool expired = false,
            string? subOverride = null,
            RSA? signingRsa = null,
            string? keyId = null)
        {
            var rsa = signingRsa ?? PrimaryRsa;
            var kid = keyId ?? PrimaryKeyId;
            var secKey = new RsaSecurityKey(rsa) { KeyId = kid };

            var claims = new List<Claim>();
            var sub = subOverride ?? (userId.HasValue ? userId.Value.ToString() : Guid.NewGuid().ToString());
            if (!string.IsNullOrEmpty(sub))
            {
                claims.Add(new Claim("sub", sub));
            }

            if (serviceToken)
            {
                claims.Add(new Claim("u", "svc"));
            }
            else
            {
                claims.Add(new Claim("u", "usr"));
            }

            var now = TimeProvider.GetUtcNow().UtcDateTime;
            var token = new JwtSecurityToken(
                issuer: issuer ?? Issuer,
                audience: audience ?? Audience,
                claims: claims,
                notBefore: now.AddMinutes(expired ? -15 : -1),
                expires: expired ? now.AddMinutes(-5) : now.AddMinutes(15),
                signingCredentials: new SigningCredentials(secKey, SecurityAlgorithms.RsaSha256));

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                PrimaryRsa.Dispose();
                RotatedRsa.Dispose();
            }
        }
    }

    private sealed class TestJwksHttpMessageHandler(SecurityTestFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = fixture.GetJwksJson();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
