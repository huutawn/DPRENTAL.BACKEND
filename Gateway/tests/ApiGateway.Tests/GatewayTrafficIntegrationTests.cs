using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Domain.Security;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Bộ kiểm thử tích hợp cho tính năng Khả năng phục hồi (Resilience) và Điều phối lưu lượng (Traffic Control):
/// 1. Upstream timeout -> Ocelot 25.0 trả 503 trong timeout budget.
/// 2. Upstream unavailable -> trả lỗi chuẩn (502/503), không crash.
/// 3. Vượt rate limit -> 429 kèm Retry-After.
/// 4. Request dưới limit -> hoạt động bình thường (200).
/// 5. Phân tầng Rate Limit (Anonymous, Authenticated, Sensitive).
/// 6. Client cancel request -> downstream request được cancel khi có thể.
/// 7. Gateway không tự ý retry lệnh nghiệp vụ không an toàn (POST, PATCH).
/// </summary>
public sealed class GatewayTrafficIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";

    [Fact]
    public async Task UpstreamTimeoutReturns503WithinTimeoutBudget()
    {
        // Tiêu chí 1: Upstream timeout -> Ocelot 25.0 trả 503 trong timeout budget.
        using RSA signingKey = RSA.Create(2048);
        // Thiết lập upstream phản hồi chậm trễ 4 giây
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"), delayMilliseconds: 4000);

        // Gateway chỉ cho phép timeout tối đa là 1 giây
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            timeoutSeconds: 1);
        using HttpClient client = factory.CreateClient();

        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/sessions");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Đảm bảo thời gian phản hồi nằm trong phạm vi ngân sách timeout (~1-2s thay vì chờ 4s)
        Assert.True(stopwatch.ElapsedMilliseconds < 3500, $"Thời gian xử lý ({stopwatch.ElapsedMilliseconds}ms) vượt quá ngân sách.");
    }

    [Fact]
    public async Task UpstreamUnavailableReturnsCanonicalErrorWithoutCrash()
    {
        // Tiêu chí 2: Upstream unavailable -> trả lỗi chuẩn (502 Bad Gateway), không crash tiến trình.
        // Trỏ tới cổng không có dịch vụ lắng nghe
        Uri unreachableAddress = new("http://127.0.0.1:1/");
        using WebApplicationFactory<Program> factory = CreateFactory(unreachableAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/sessions");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ExceedingAnonymousRateLimitReturns429TooManyRequests()
    {
        // Tiêu chí 3 & 4: Vượt rate limit -> 429. Request dưới limit -> 200 OK.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"));

        // Cấu hình Anonymous Rate Limit là 2 request / 60 giây
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            anonymousPermitLimit: 2);
        using HttpClient client = factory.CreateClient();

        // Request 1: Dưới limit -> 200 OK
        using HttpResponseMessage response1 = await client.PostAsync(
            "/api/v1/auth/public-endpoint",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Request 2: Đạt limit -> 200 OK
        using HttpResponseMessage response2 = await client.PostAsync(
            "/api/v1/auth/public-endpoint",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Request 3: Vượt limit -> 429 Too Many Requests
        using HttpResponseMessage response3 = await client.PostAsync(
            "/api/v1/auth/public-endpoint",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response3.StatusCode);
        GatewayErrorResponse? error = await response3.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(GatewayError.RateLimitExceeded.Code, error.Code);
        Assert.True(response3.Headers.Contains(HeaderNames.RetryAfter));
    }

    [Fact]
    public async Task SensitiveRoutesEnforceStrictRateLimitPolicy()
    {
        // Phân tầng Sensitive Routes (ví dụ /api/v1/auth/sessions): Giới hạn nghiêm ngặt hơn
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"));

        // Cấu hình Sensitive Rate Limit là 1 request / 60 giây
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            sensitivePermitLimit: 1);
        using HttpClient client = factory.CreateClient();

        // Request 1 tới sensitive endpoint -> 200 OK
        using HttpResponseMessage response1 = await client.PostAsync(
            "/api/v1/auth/sessions",
            new StringContent("{\"email\":\"user@test.com\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Request 2 tới sensitive endpoint -> 429 Too Many Requests
        using HttpResponseMessage response2 = await client.PostAsync(
            "/api/v1/auth/sessions",
            new StringContent("{\"email\":\"user@test.com\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserBenefitsFromHigherRateLimitTier()
    {
        // Phân tầng Authenticated User: Phân vùng theo UserId và có hạn mức cao hơn Anonymous
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"));

        // Anonymous = 1, Authenticated = 3
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            includeProtectedRoute: true,
            anonymousPermitLimit: 1,
            authenticatedPermitLimit: 3);
        using HttpClient client = factory.CreateClient();

        string userId = Guid.NewGuid().ToString("D");
        string validToken = CreateToken(signingKey, "key-1", Issuer, Audience, userId);

        // Request 1 với token hợp lệ -> 200 OK
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/protected/orders");
        req1.Headers.Authorization = new("Bearer", validToken);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Request 2 với cùng token -> 200 OK (vượt quá Anonymous limit 1 nhưng dưới Authenticated limit 3)
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/protected/orders");
        req2.Headers.Authorization = new("Bearer", validToken);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // Request 3 với cùng token -> 200 OK
        using HttpRequestMessage req3 = new(HttpMethod.Get, "/protected/orders");
        req3.Headers.Authorization = new("Bearer", validToken);
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.OK, res3.StatusCode);

        // Request 4 với cùng token -> 429 Too Many Requests (đã dùng hết hạn mức 3)
        using HttpRequestMessage req4 = new(HttpMethod.Get, "/protected/orders");
        req4.Headers.Authorization = new("Bearer", validToken);
        using HttpResponseMessage res4 = await client.SendAsync(req4);
        Assert.Equal(HttpStatusCode.TooManyRequests, res4.StatusCode);
    }

    [Fact]
    public async Task ClientCancellationIsHandledGracefullyWithoutCrashing()
    {
        // Tiêu chí 5: Client cancel request -> xử lý hủy an toàn, không sinh log lỗi crash.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"), delayMilliseconds: 3000);
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using CancellationTokenSource clientCts = new(TimeSpan.FromMilliseconds(150));

        // Client chủ động hủy request sau 150ms
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await client.GetAsync("/api/v1/auth/sessions", clientCts.Token);
        });

        // Gateway vẫn hoạt động bình thường, phục vụ tốt request tiếp theo
        using HttpResponseMessage healthResponse = await client.GetAsync("/_health/live");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
    }

    [Fact]
    public async Task UnsafeBusinessCommandIsNotRetriedAutomatically()
    {
        // Tiêu chí 6: Gateway không retry business command tùy tiện (POST /rentals không tự gửi lại khi gặp lỗi).
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackResilienceService upstream = new(CreateJwks(signingKey, "key-1"), delayMilliseconds: 2500);

        // Cấu hình timeout 1 giây
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            timeoutSeconds: 1);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"action\":\"create_rental\"}", Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        // Request bị timeout 503 trong Ocelot 25.0
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Đảm bảo downstream service chỉ nhận đúng 1 lần gọi, Gateway không retry thêm lần nào
        Assert.Equal(1, upstream.TotalRequestsReceived);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Uri upstreamAddress,
        bool includeProtectedRoute = false,
        int? timeoutSeconds = null,
        int? anonymousPermitLimit = null,
        int? authenticatedPermitLimit = null,
        int? sensitivePermitLimit = null)
    {
        Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
        {
            ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
            ["Gateway:Authentication:Jwt:Audience"] = Audience,
            ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, ".well-known/jwks.json").AbsoluteUri,
            ["Routes:0:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:0:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:0:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:1:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:1:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:1:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:2:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:2:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:2:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:3:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:3:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:3:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (timeoutSeconds.HasValue)
        {
            overrides["GlobalConfiguration:Timeout"] = timeoutSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (anonymousPermitLimit.HasValue)
        {
            overrides["Gateway:Traffic:RateLimiting:Anonymous:PermitLimit"] = anonymousPermitLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            overrides["Gateway:Traffic:RateLimiting:Anonymous:WindowSeconds"] = "60";
        }

        if (authenticatedPermitLimit.HasValue)
        {
            overrides["Gateway:Traffic:RateLimiting:Authenticated:PermitLimit"] = authenticatedPermitLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            overrides["Gateway:Traffic:RateLimiting:Authenticated:WindowSeconds"] = "60";
        }

        if (sensitivePermitLimit.HasValue)
        {
            overrides["Gateway:Traffic:RateLimiting:Sensitive:PermitLimit"] = sensitivePermitLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            overrides["Gateway:Traffic:RateLimiting:Sensitive:WindowSeconds"] = "60";
        }

        if (includeProtectedRoute)
        {
            overrides["Routes:4:Key"] = "protected-service";
            overrides["Routes:4:UpstreamPathTemplate"] = "/protected/{everything}";
            overrides["Routes:4:DownstreamPathTemplate"] = "/protected/{everything}";
            overrides["Routes:4:DownstreamScheme"] = upstreamAddress.Scheme;
            overrides["Routes:4:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host;
            overrides["Routes:4:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides)));
    }

    private static string CreateToken(RSA signingKey, string keyId, string issuer, string audience, string userId)
    {
        RsaSecurityKey key = new(signingKey)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        JwtSecurityToken token = new(
            issuer,
            audience,
            [new Claim(JwtRegisteredClaimNames.Sub, userId)],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
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

    private sealed class LoopbackResilienceService : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task acceptingTask;
        private readonly int delayMilliseconds;

        internal LoopbackResilienceService(string jwks, int delayMilliseconds = 0)
        {
            Jwks = jwks;
            this.delayMilliseconds = delayMilliseconds;
            listener.Prefixes.Add($"http://127.0.0.1:{GetAvailablePort()}/");
            listener.Start();
            BaseAddress = new Uri(listener.Prefixes.Single());
            acceptingTask = AcceptRequestsAsync();
        }

        internal Uri BaseAddress { get; }

        internal string Jwks { get; set; }

        internal int TotalRequestsReceived { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await cancellationTokenSource.CancelAsync();
            listener.Close();
            try
            {
                await acceptingTask;
            }
            catch (HttpListenerException)
            {
                // Unblocks GetContextAsync
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }

        private async Task AcceptRequestsAsync()
        {
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationTokenSource.Token);
                    _ = HandleRequestAsync(context);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on disposal
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            if (string.Equals(request.Url?.AbsolutePath, "/.well-known/jwks.json", StringComparison.Ordinal))
            {
                await WriteResponseAsync(context.Response, Jwks, "application/json");
                return;
            }

            TotalRequestsReceived++;

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationTokenSource.Token);
            }

            await WriteResponseAsync(context.Response, "{\"status\":\"ok\"}", "application/json");
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, string content, string contentType)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static int GetAvailablePort()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }
}
