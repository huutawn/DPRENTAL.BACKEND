using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Observability;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Bộ kiểm thử tích hợp cho tính năng Giám sát vận hành (Observability & Operational Readiness):
/// 1. Mỗi request đều có structured log (timestamp, correlationId, traceId, route, method, status, duration, upstream).
/// 2. Gateway và downstream dùng chung correlationId và traceId (W3C traceparent).
/// 3. Timeout kích hoạt tăng metric upstream_timeout_total.
/// 4. 429 kích hoạt tăng metric rate_limit_total.
/// 5. /health/live hoạt động và trả về trạng thái Healthy.
/// 6. /health/ready phản ánh tính sẵn sàng của JWKS và cấu hình Ocelot.
/// 7. Tuyệt đối không rò rỉ token, password hay secret trong log.
/// </summary>
public sealed class GatewayObservabilityIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";

    [Fact]
    public async Task EveryRequestProducesStructuredLogWithoutSensitiveDataLeakage()
    {
        // Tiêu chí 1 & 7: Mỗi request có structured log và không rò rỉ token/password
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"));

        TestLogSink logSink = new();
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, logSink: logSink);
        using HttpClient client = factory.CreateClient();

        string sensitiveToken = "super-secret-jwt-token-12345";
        string sensitivePassword = "my-super-secret-password-999";

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent(
                $"{{\"email\":\"user@example.test\",\"password\":\"{sensitivePassword}\"}}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {sensitiveToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Kiểm tra log có cấu trúc được ghi lại
        string? structuredLog = logSink.Messages.FirstOrDefault(m => m.Contains("auth-service", StringComparison.Ordinal));
        Assert.NotNull(structuredLog);
        Assert.Contains("POST", structuredLog, StringComparison.Ordinal);
        Assert.Contains("200", structuredLog, StringComparison.Ordinal);
        Assert.Contains("TraceId=", structuredLog, StringComparison.Ordinal);
        Assert.Contains("CorrelationId=", structuredLog, StringComparison.Ordinal);
        Assert.Contains("Upstream=auth-service", structuredLog, StringComparison.Ordinal);

        // Tiêu chí 7: Tuyệt đối không tìm thấy token hoặc password trong bất kỳ dòng log nào
        foreach (string logMessage in logSink.Messages)
        {
            Assert.DoesNotContain(sensitiveToken, logMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitivePassword, logMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GatewayAndDownstreamShareSameCorrelationIdAndTraceId()
    {
        // Tiêu chí 2: Gateway và downstream cùng correlationId và traceId
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        Guid correlationId = Guid.NewGuid();
        const string inboundTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/sessions");
        request.Headers.Add(GatewayHeaderConstants.CorrelationId, correlationId.ToString("D"));
        request.Headers.TryAddWithoutValidation("traceparent", inboundTraceParent);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Kiểm tra correlationId đồng nhất giữa request, upstream và response header
        Assert.Equal(correlationId.ToString("D"), upstream.LastRequest.Headers[GatewayHeaderConstants.CorrelationId]);
        Assert.Equal(correlationId.ToString("D"), response.Headers.GetValues(GatewayHeaderConstants.CorrelationId).Single());

        // Kiểm tra W3C TraceId đồng nhất
        string upstreamTraceParent = upstream.LastRequest.Headers["traceparent"];
        Assert.False(string.IsNullOrWhiteSpace(upstreamTraceParent));
        Assert.Contains("4bf92f3577b34da6a3ce929d0e0e4736", upstreamTraceParent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutIncrementsUpstreamTimeoutMetric()
    {
        // Tiêu chí 3: Timeout có metric
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"), delayMilliseconds: 3000);

        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, timeoutSeconds: 1);
        using HttpClient client = factory.CreateClient();

        long timeoutCount = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayMetricNames.MeterName &&
                instrument.Name == GatewayMetricNames.UpstreamTimeoutTotal)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == GatewayMetricNames.UpstreamTimeoutTotal)
            {
                Interlocked.Add(ref timeoutCount, measurement);
            }
        });
        listener.Start();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/sessions");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        listener.RecordObservableInstruments();

        Assert.True(timeoutCount >= 1, "Metric gateway_upstream_timeout_total phải được tăng khi xảy ra timeout.");
    }

    [Fact]
    public async Task RateLimitExceededIncrementsRateLimitMetric()
    {
        // Tiêu chí 4: 429 có metric
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"));

        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, anonymousPermitLimit: 1);
        using HttpClient client = factory.CreateClient();

        long rateLimitCount = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayMetricNames.MeterName &&
                instrument.Name == GatewayMetricNames.RateLimitTotal)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == GatewayMetricNames.RateLimitTotal)
            {
                Interlocked.Add(ref rateLimitCount, measurement);
            }
        });
        listener.Start();

        // Request 1: 200 OK
        using HttpResponseMessage res1 = await client.GetAsync("/api/v1/auth/public-endpoint");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Request 2: 429 Too Many Requests
        using HttpResponseMessage res2 = await client.GetAsync("/api/v1/auth/public-endpoint");
        Assert.Equal(HttpStatusCode.TooManyRequests, res2.StatusCode);
        listener.RecordObservableInstruments();

        Assert.True(rateLimitCount >= 1, "Metric gateway_rate_limit_total phải được tăng khi vượt hạn mức tần suất.");
    }

    [Fact]
    public async Task HealthLiveEndpointReturnsHealthy()
    {
        // Tiêu chí 5: Health live chạy (/health/live và /_health/live)
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response1 = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        string content1 = await response1.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content1);

        using HttpResponseMessage response2 = await client.GetAsync("/_health/live");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task HealthReadyEndpointReflectsJwksAndProxyConfigurationDependency()
    {
        // Tiêu chí 6: Readiness phản ánh config/dependency cần thiết (/health/ready)
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackObservabilityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response1 = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        string content1 = await response1.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", content1);

        using HttpResponseMessage response2 = await client.GetAsync("/_health/ready");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [Fact]
    public async Task HealthReadyEndpointFailsWhenJwksIsUnavailable()
    {
        // Tiêu chí 6 (Edge case): Khi AuthService JWKS không khả dụng -> Readiness báo Unhealthy (503)
        using WebApplicationFactory<Program> factory = CreateFactory(new Uri("http://127.0.0.1:1/"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", content);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Uri upstreamAddress,
        TestLogSink? logSink = null,
        int? timeoutSeconds = null,
        int? anonymousPermitLimit = null)
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

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides));
                if (logSink is not null)
                {
                    builder.ConfigureLogging(logging => logging.AddProvider(logSink));
                }
            });
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

    private sealed class TestLogSink : ILoggerProvider, ILogger
    {
        private readonly ConcurrentBag<string> messages = new();
        public IEnumerable<string> Messages => messages;

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class LoopbackObservabilityService : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task acceptingTask;
        private readonly int delayMilliseconds;

        internal LoopbackObservabilityService(string jwks, int delayMilliseconds = 0)
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
        internal CapturedRequest LastRequest { get; private set; } = new();

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

            Dictionary<string, string> capturedHeaders = new(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in request.Headers.AllKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    capturedHeaders[key] = request.Headers[key] ?? string.Empty;
                }
            }

            LastRequest = new CapturedRequest
            {
                Method = request.HttpMethod,
                Path = request.Url?.AbsolutePath,
                Headers = capturedHeaders
            };

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationTokenSource.Token);
            }

            await WriteResponseAsync(context.Response, "{\"success\":true}", "application/json");
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

    private sealed class CapturedRequest
    {
        public string? Method { get; set; }
        public string? Path { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
