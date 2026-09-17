using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Authentication;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class GatewayIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";

    [Fact]
    public async Task AuthRouteRemovesPrefixAndKeepsCorrelationId()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackAuthService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        Guid correlationId = Guid.NewGuid();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions?source=test")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Correlation-ID", correlationId.ToString("D"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/sessions", upstream.LastRequest.Path);
        Assert.Equal("?source=test", upstream.LastRequest.Query);
        Assert.Equal(correlationId.ToString("D"), upstream.LastRequest.CorrelationId);
        Assert.Equal(correlationId.ToString("D"), response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task SwaggerRoutesForwardDirectlyToAuthService()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackAuthService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage responseJson = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, responseJson.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest.Path);

        using HttpResponseMessage responseUi = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, responseUi.StatusCode);
        Assert.Equal("/swagger/index.html", upstream.LastRequest.Path);

        using HttpResponseMessage responseRoot = await client.GetAsync("/swagger");
        Assert.Equal(HttpStatusCode.OK, responseRoot.StatusCode);
        Assert.Equal("/swagger", upstream.LastRequest.Path);
    }

    [Fact]
    public async Task AuthRoutePropagatesW3CTraceContextToUpstream()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackAuthService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        const string inboundTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/sessions");
        request.Headers.TryAddWithoutValidation("traceparent", inboundTraceParent);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(ActivityContext.TryParse(inboundTraceParent, null, out ActivityContext inboundContext));
        Assert.True(ActivityContext.TryParse(upstream.LastRequest.TraceParent, null, out ActivityContext upstreamContext));
        // YARP phải tạo span con mới nhưng vẫn giữ TraceId gốc để collector ghép Gateway và downstream vào một trace.
        Assert.Equal(inboundContext.TraceId, upstreamContext.TraceId);
        Assert.NotEqual(inboundContext.SpanId, upstreamContext.SpanId);
        Assert.True(upstreamContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
    }

    [Fact]
    public async Task ProtectedRouteRejectsInvalidTokensAndForwardsValidBearerToken()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackAuthService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage anonymousResponse = await client.GetAsync("/protected/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        GatewayErrorResponse? missingTokenError = await anonymousResponse.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(missingTokenError);
        Assert.Equal("Gateway.Authentication.Required", missingTokenError.Code);

        string wrongAudienceToken = CreateToken(signingKey, "key-1", Issuer, "other-audience");
        using HttpRequestMessage invalidRequest = new(HttpMethod.Get, "/protected/orders");
        invalidRequest.Headers.Authorization = new("Bearer", wrongAudienceToken);
        using HttpResponseMessage invalidResponse = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        GatewayErrorResponse? invalidTokenError = await invalidResponse.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(invalidTokenError);
        Assert.Equal("Gateway.Authentication.InvalidToken", invalidTokenError.Code);

        string validToken = CreateToken(signingKey, "key-1", Issuer, Audience);
        using HttpRequestMessage validRequest = new(HttpMethod.Get, "/protected/orders");
        validRequest.Headers.Authorization = new("Bearer", validToken);
        using HttpResponseMessage validResponse = await client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal($"Bearer {validToken}", upstream.LastRequest.Authorization);
    }

    [Fact]
    public async Task ProxyTransportFailureUsesCanonicalGatewayError()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(new Uri("http://127.0.0.1:1/"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/login");
        GatewayErrorResponse? error = await response.Content.ReadFromJsonAsync<GatewayErrorResponse>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("Gateway.Upstream.Unavailable", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    [Fact]
    public async Task HealthEndpointsSeparateProcessLivenessFromJwksReadiness()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackAuthService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage liveness = await client.GetAsync("/_health/live");
        using HttpResponseMessage readiness = await client.GetAsync("/_health/ready");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.True(upstream.JwksRequestCount > 0);
    }

    [Fact]
    public void InvalidJwtConfigurationStopsApplicationStartup()
    {
        Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
        {
            ["Gateway:Authentication:Jwt:Issuer"] = string.Empty,
            ["Gateway:Authentication:Jwt:Audience"] = string.Empty,
            ["Gateway:Authentication:Jwt:JwksUri"] = "not-a-uri"
        };
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides)));

        Assert.ThrowsAny<Exception>(factory.CreateClient);
    }

    private static WebApplicationFactory<Program> CreateFactory(Uri upstreamAddress, bool includeProtectedRoute = false)
    {
        Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
        {
            ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
            ["Gateway:Authentication:Jwt:Audience"] = Audience,
            ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, ".well-known/jwks.json").AbsoluteUri,
            ["ReverseProxy:Clusters:auth-service:Destinations:primary:Address"] = upstreamAddress.AbsoluteUri
        };

        if (includeProtectedRoute)
        {
            overrides["ReverseProxy:Routes:protected-service:ClusterId"] = "auth-service";
            overrides["ReverseProxy:Routes:protected-service:Match:Path"] = "/protected/{**catch-all}";
        }

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides)));
    }

    private static string CreateToken(RSA signingKey, string keyId, string issuer, string audience)
    {
        RsaSecurityKey key = new(signingKey)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        JwtSecurityToken token = new(
            issuer,
            audience,
            [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString("D"))],
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

    private sealed class LoopbackAuthService : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task acceptingTask;

        internal LoopbackAuthService(string jwks)
        {
            Jwks = jwks;
            listener.Prefixes.Add($"http://127.0.0.1:{GetAvailablePort()}/");
            listener.Start();
            BaseAddress = new Uri(listener.Prefixes.Single());
            acceptingTask = AcceptRequestsAsync();
        }

        internal Uri BaseAddress { get; }

        internal string Jwks { get; set; }

        internal int JwksRequestCount { get; private set; }

        internal ReceivedRequest LastRequest { get; private set; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

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
                // Closing HttpListener unblocks GetContextAsync.
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
                // Expected when the test server is disposed.
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            if (string.Equals(request.Url?.AbsolutePath, "/.well-known/jwks.json", StringComparison.Ordinal))
            {
                JwksRequestCount++;
                await WriteResponseAsync(context.Response, Jwks, "application/json");
                return;
            }

            LastRequest = new ReceivedRequest(
                request.Url?.AbsolutePath ?? string.Empty,
                request.Url?.Query ?? string.Empty,
                request.Headers["Authorization"] ?? string.Empty,
                request.Headers["X-Correlation-ID"] ?? string.Empty,
                request.Headers["traceparent"] ?? string.Empty);
            await WriteResponseAsync(context.Response, "{\"proxied\":true}", "application/json");
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

    private sealed record ReceivedRequest(string Path, string Query, string Authorization, string CorrelationId, string TraceParent);
}
