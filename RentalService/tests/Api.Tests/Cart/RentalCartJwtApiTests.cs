using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Kafka.Producers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Api.Controllers;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

public sealed class RentalCartJwtApiTests : IClassFixture<RentalCartJwtApiTests.JwtApiFactory>
{
    private readonly JwtApiFactory _factory;

    public RentalCartJwtApiTests(JwtApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Cart_ValidRenterJwt_AllowsAccessAndUsesSubAsRenterIdentity()
    {
        Guid renterId = Guid.NewGuid();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(renterId));

        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        using HttpResponseMessage add = await client.PostAsJsonAsync(
            "rental/cart/items",
            new AddCartItemRequest(Guid.NewGuid(), 1, start, start.AddDays(2)));

        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        using HttpResponseMessage get = await client.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Cart_RejectsMissingInvalidAndServiceTokens()
    {
        using HttpClient anonymous = _factory.CreateClient();
        using HttpResponseMessage missing = await anonymous.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(Guid.NewGuid(), audience: "inventory-api"));
        using HttpResponseMessage wrongAudience = await anonymous.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtApiFactory.CreateTokenWithInvalidSignature(Guid.NewGuid()));
        using HttpResponseMessage invalidSignature = await anonymous.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidSignature.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(Guid.NewGuid(), expired: true));
        using HttpResponseMessage expired = await anonymous.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(Guid.NewGuid(), serviceToken: true));
        using HttpResponseMessage serviceToken = await anonymous.GetAsync("rental/cart");
        Assert.Equal(HttpStatusCode.Forbidden, serviceToken.StatusCode);
    }

    public sealed class JwtApiFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "dprental-authservice-dev";
        private const string Audience = "dprental-clients";
        private const string KeyId = "rental-cart-test-key";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly FakeCartRepository _carts = new();
        private readonly FakeInventoryReaders _inventory = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=rental-test;Username=test;Password=test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=rental-test;Username=test;Password=test",
                    ["Authentication:Jwt:Issuer"] = Issuer,
                    ["Authentication:Jwt:Audience"] = Audience,
                    ["Authentication:Jwt:JwksUri"] = "http://localhost/test-jwks",
                    ["InternalAuthentication:ApiKey"] = "dev-internal-api-key-dprental-2026"
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
            var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
            var claims = new List<Claim> { new("sub", subject.ToString()) };
            if (serviceToken)
            {
                claims.Add(new Claim("u", "svc"));
                claims.Add(new Claim("s", "inventory.availability.read"));
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
