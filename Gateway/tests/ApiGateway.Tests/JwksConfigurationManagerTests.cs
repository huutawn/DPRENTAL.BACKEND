using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Authentication;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class JwksConfigurationManagerTests
{
    [Fact]
    public async Task RefreshLoadsRotatedKeyAndKeepsLastKnownGoodConfigurationDuringOutage()
    {
        using RSA firstKey = RSA.Create(2048);
        using RSA secondKey = RSA.Create(2048);
        string jwks = CreateJwks(firstKey, "first");
        bool available = true;
        using HttpClient client = new(new DelegateHttpMessageHandler(_ =>
        {
            return available
                ? CreateJsonResponse(jwks)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        MutableTimeProvider clock = new(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        JwksConfigurationManager manager = new(
            client,
            new GatewayJwtOptions { Issuer = "issuer", Audience = "audience", JwksUri = "http://auth.example.test/.well-known/jwks.json" },
            clock);

        Assert.Contains((await manager.GetConfigurationAsync(CancellationToken.None)).SigningKeys, key => key.KeyId == "first");

        jwks = CreateJwks(secondKey, "second");
        manager.RequestRefresh();
        Assert.Contains((await manager.GetConfigurationAsync(CancellationToken.None)).SigningKeys, key => key.KeyId == "second");

        available = false;
        clock.Advance(TimeSpan.FromMinutes(11));
        manager.RequestRefresh();
        Assert.Contains((await manager.GetConfigurationAsync(CancellationToken.None)).SigningKeys, key => key.KeyId == "second");
        Assert.False(manager.HasUsableConfiguration());
    }

    private static HttpResponseMessage CreateJsonResponse(string content)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
        return response;
    }

    private static string CreateJwks(RSA rsa, string keyId)
    {
        RSAParameters parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    kid = keyId,
                    use = "sig",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        });
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        internal void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
