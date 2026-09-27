using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.MediaService.Api.Security;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class JwksConfigurationManagerTests
{
    [Fact]
    public async Task GetConfigurationAsync_ShouldLoadAndCacheRsaSigningKey()
    {
        using RSA rsa = RSA.Create(2048);
        RSAParameters publicKey = rsa.ExportParameters(false);
        string jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", kid = "key-1", use = "sig", alg = "RS256",
                    n = Base64UrlEncoder.Encode(publicKey.Modulus),
                    e = Base64UrlEncoder.Encode(publicKey.Exponent)
                }
            }
        });
        var handler = new StaticJwksHandler(jwks);
        using var client = new HttpClient(handler);
        using var manager = new JwksConfigurationManager(
            client,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                Issuer = "issuer",
                Audience = "audience",
                JwksUri = "https://auth.test/.well-known/jwks.json"
            }));

        var first = await manager.GetConfigurationAsync(CancellationToken.None);
        var second = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal("key-1", Assert.Single(first.SigningKeys).KeyId);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StaticJwksHandler(string jwks) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwks)
            });
        }
    }
}
