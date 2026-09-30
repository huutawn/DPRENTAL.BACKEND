using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>Checks proxy timeout, cancellation, and retry behavior.</summary>
public sealed class GatewayResilienceIntegrationTests
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
        int? timeoutSeconds = null)
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

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides)));
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
