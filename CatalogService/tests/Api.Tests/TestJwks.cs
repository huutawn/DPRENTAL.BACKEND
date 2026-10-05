using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.CatalogService.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>Giả lập JWKS endpoint của AuthService cho test, không mở cổng mạng thật.</summary>
public static class TestJwks
{
    public const string Uri = "http://auth-service.test/.well-known/jwks.json";

    /// <summary>Cấu hình host test tải JWKS từ handler giả thay cho AuthService.</summary>
    public static IWebHostBuilder UseTestJwks(this IWebHostBuilder builder, HttpMessageHandler? handler = null)
    {
        builder.UseSetting("Auth:Jwt:JwksUri", Uri);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(JwtBearerServiceCollectionExtensions.JwksHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler ?? new StubJwksHandler()));
        return builder;
    }

    /// <summary>Tạo JWKS document theo đúng shape AuthService công bố (kty, kid, use, alg, n, e).</summary>
    public static string CreateDocument(params (string KeyId, RSA Rsa)[] keys)
    {
        var jwks = keys.Select(key =>
        {
            RSAParameters parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
            return new
            {
                kty = "RSA",
                kid = key.KeyId,
                use = "sig",
                alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        });

        return JsonSerializer.Serialize(new { keys = jwks });
    }

    /// <summary>Handler trả JWKS do test điều khiển và đếm số lần Catalog thật sự gọi sang AuthService.</summary>
    public sealed class StubJwksHandler : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        /// <summary>Nội dung JWKS trả về; mặc định chứa key ký của <see cref="TestJwtHelper"/>.</summary>
        public string Document { get; set; } = CreateDocument((TestJwtHelper.DefaultKeyId, TestJwtHelper.RsaInstance));

        /// <summary>Đặt false để giả lập AuthService không truy cập được.</summary>
        public bool Available { get; set; } = true;

        public TimeSpan? MaxAge { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            if (!Available)
            {
                throw new HttpRequestException("AuthService không khả dụng.");
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(Document, Encoding.UTF8, "application/json")
            };
            if (MaxAge is not null)
            {
                response.Headers.CacheControl = new() { MaxAge = MaxAge };
            }

            return Task.FromResult(response);
        }
    }
}
