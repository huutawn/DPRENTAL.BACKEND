using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

public sealed class OrderHubAuthenticationIntegrationTests : IClassFixture<OrderHubAuthenticationIntegrationTests.HubApiFactory>
{
    private readonly HubApiFactory _factory;

    public OrderHubAuthenticationIntegrationTests(HubApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Negotiate_WithValidCookie_Returns200_WithNegotiateResponse()
    {
        Guid userId = Guid.NewGuid();
        string token = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("connectionId", out _) || doc.RootElement.TryGetProperty("connectionToken", out _));
        Assert.True(doc.RootElement.TryGetProperty("negotiateVersion", out _));
    }

    [Fact]
    public async Task Negotiate_WithValidAuthorizationHeader_Returns200()
    {
        Guid userId = Guid.NewGuid();
        string token = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_Anonymous_Returns401()
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_ServiceToken_ViaHeader_Returns403()
    {
        Guid serviceId = Guid.NewGuid();
        string serviceToken = _factory.CreateToken(serviceId, serviceToken: true);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_ServiceToken_ViaCookie_Returns403()
    {
        Guid serviceId = Guid.NewGuid();
        string serviceToken = _factory.CreateToken(serviceId, serviceToken: true);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={serviceToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_ExpiredToken_Returns401()
    {
        string expiredToken = _factory.CreateToken(Guid.NewGuid(), expired: true);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={expiredToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_InvalidSignatureToken_Returns401()
    {
        string invalidToken = HubApiFactory.CreateTokenWithInvalidSignature(Guid.NewGuid());

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={invalidToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_MalformedSubject_Returns401Or403()
    {
        string malformedToken = _factory.CreateTokenWithSubject("not-a-guid");

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", $"accessToken={malformedToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Negotiate_QueryTokenAlone_IsRejectedWith401()
    {
        Guid userId = Guid.NewGuid();
        string token = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();
        // Query token only, no Authorization header, no accessToken cookie
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1&access_token={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_HeaderPrecedence_InvalidHeaderWithValidCookie_Returns401()
    {
        Guid userId = Guid.NewGuid();
        string validCookieToken = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-or-malformed-token");
        request.Headers.Add("Cookie", $"accessToken={validCookieToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RestEndpoint_WithCookieAlone_Returns401()
    {
        Guid userId = Guid.NewGuid();
        string token = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/rental/orders");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        // Cookie MUST NOT grant access to REST endpoints
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_BrowserOriginAllowlist_AllowedReturns200_UnallowedReturns403()
    {
        Guid userId = Guid.NewGuid();
        string token = _factory.CreateToken(userId);

        using HttpClient client = _factory.CreateClient();

        // 1. Unallowed origin -> 403 Forbidden
        using var unallowedReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        unallowedReq.Headers.Add("Cookie", $"accessToken={token}");
        unallowedReq.Headers.Add("Origin", "http://untrusted-site.com");
        using HttpResponseMessage unallowedResp = await client.SendAsync(unallowedReq);
        Assert.Equal(HttpStatusCode.Forbidden, unallowedResp.StatusCode);

        // 2. Allowed origin -> 200 OK
        using var allowedReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        allowedReq.Headers.Add("Cookie", $"accessToken={token}");
        allowedReq.Headers.Add("Origin", "http://localhost:3000");
        using HttpResponseMessage allowedResp = await client.SendAsync(allowedReq);
        Assert.Equal(HttpStatusCode.OK, allowedResp.StatusCode);

        // 3. Non-browser (no Origin) -> 200 OK (passed to JWT authorization)
        using var nonBrowserReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/rental/hubs/orders/negotiate?negotiateVersion=1");
        nonBrowserReq.Headers.Add("Cookie", $"accessToken={token}");
        using HttpResponseMessage nonBrowserResp = await client.SendAsync(nonBrowserReq);
        Assert.Equal(HttpStatusCode.OK, nonBrowserResp.StatusCode);
    }

    public sealed class HubApiFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "dprental-authservice-dev";
        private const string Audience = "dprental-clients";
        private const string KeyId = "rental-hub-test-key";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly FakeCartRepository _carts = new();
        private readonly FakeInventoryReaders _inventory = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=rental-hub-test;Username=test;Password=test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=rental-hub-test;Username=test;Password=test",
                    ["Authentication:Jwt:Issuer"] = Issuer,
                    ["Authentication:Jwt:Audience"] = Audience,
                    ["Authentication:Jwt:JwksUri"] = "http://localhost/test-jwks",
                    ["InternalAuthentication:ApiKey"] = "dev-internal-api-key-dprental-2026",
                    ["OrderHub:HubPath"] = "/api/v1/rental/hubs/orders",
                    ["OrderHub:AllowedOrigins:0"] = "http://localhost:3000",
                    ["OrderHub:AllowedOrigins:1"] = "https://dprental.com"
                }));

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = Audience;
                    options.ConfigurationManager = new TestJwksConfigurationManager(_rsa, KeyId);
                });
                services.RemoveAll<IRentalCartRepository>();
                services.AddSingleton<IRentalCartRepository>(_carts);
                services.AddSingleton<IRentalListingReader>(_inventory);
                services.AddSingleton<IRentalAvailabilityReader>(_inventory);
                services.AddSingleton<IOrderLockManager>(new FakeOrderLockManager());
                services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
                services.AddSingleton<IRentalEventProducer>(new FakeRentalEventProducer());
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "dev-internal-api-key-dprental-2026");
        }

        public string CreateToken(
            Guid subject,
            string audience = Audience,
            bool serviceToken = false,
            bool expired = false)
        {
            return CreateTokenWithSubject(subject.ToString(), audience, serviceToken, expired);
        }

        public string CreateTokenWithSubject(
            string subject,
            string audience = Audience,
            bool serviceToken = false,
            bool expired = false)
        {
            var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
            var claims = new List<Claim> { new("sub", subject) };
            if (serviceToken)
            {
                claims.Add(new Claim("u", "svc"));
                claims.Add(new Claim("s", "rental.orders.read"));
            }

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.UtcNow.AddMinutes(expired ? -10 : -1),
                expires: expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public static string CreateTokenWithInvalidSignature(Guid subject)
        {
            using RSA otherKey = RSA.Create(2048);
            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: [new Claim("sub", subject.ToString())],
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(
                    new RsaSecurityKey(otherKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _rsa.Dispose();
        }

        private sealed class TestJwksConfigurationManager(RSA rsa, string keyId)
            : IConfigurationManager<OpenIdConnectConfiguration>
        {
            private readonly OpenIdConnectConfiguration _configuration = CreateConfiguration(rsa, keyId);

            public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
                => Task.FromResult(_configuration);

            public void RequestRefresh() { }

            private static OpenIdConnectConfiguration CreateConfiguration(RSA rsa, string keyId)
            {
                var configuration = new OpenIdConnectConfiguration();
                configuration.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
                return configuration;
            }
        }
    }

    private sealed class FakeCartRepository : IRentalCartRepository
    {
        private readonly ConcurrentDictionary<Guid, RentalCart> _carts = new();

        public Task<RentalCart?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _carts.TryGetValue(id, out RentalCart? cart);
            return Task.FromResult(cart);
        }

        public Task<RentalCart?> GetActiveByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
            => Task.FromResult<RentalCart?>(System.Linq.Enumerable.FirstOrDefault(
                _carts.Values, cart => cart.RenterId == renterId));

        public Task<RentalCart?> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
            => Task.FromResult<RentalCart?>(System.Linq.Enumerable.FirstOrDefault(
                _carts.Values, cart => cart.RenterId == renterId));

        public Task<RentalCart> CreateAsync(RentalCart cart, CancellationToken cancellationToken = default)
        {
            _carts[cart.Id] = cart;
            return Task.FromResult(cart);
        }

        public Task<bool> UpdateAsync(RentalCart cart, CancellationToken cancellationToken = default)
        {
            _carts[cart.Id] = cart;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_carts.TryRemove(id, out _));
    }

    private sealed class FakeInventoryReaders : IRentalListingReader, IRentalAvailabilityReader
    {
        public Task<Result<RentalListingSnapshot>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success(new RentalListingSnapshot(
                listingId, Guid.NewGuid(), Guid.NewGuid(), true, true, 100m, 50m, "v1")));

        public Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
            Guid listingId,
            DateOnly startDate,
            DateOnly endDate,
            int requestedQuantity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success(new RentalAvailabilityResult(10, true)));
    }
}
