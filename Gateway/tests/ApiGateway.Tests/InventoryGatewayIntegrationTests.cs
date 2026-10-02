using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Authentication;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class InventoryGatewayIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string HumanAudience = "dprental-clients";
    private const string ServiceAudience = "inventory-api";

    [Fact]
    public async Task InventoryRouteValidHumanBearerTokenProxiesAndStripsCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/assets/123");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Cookie", "session=12345; other=cookie");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/assets/123", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task InventoryRouteValidServiceTokenProxiesSuccessfully()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateServiceToken(signingKey, "key-1", Issuer, ServiceAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/inventory/allocations/hold");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/v1/inventory/allocations/hold", upstream.LastRequest?.Path);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
    }

    [Fact]
    public async Task NonInventoryRouteServiceTokenWithInventoryAudienceIsRejectedWith401()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateServiceToken(signingKey, "key-1", Issuer, ServiceAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/media/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request);

        // Must reject because inventory-api audience is isolated from non-inventory routes!
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InventoryRouteAccessTokenCookieConvertsToBearerAndStripsCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/calendar");
        request.Headers.Add("Cookie", $"accessToken={token}; theme=dark");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task InventoryRouteSpoofedHeadersAreStrippedBeforeDownstream()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, Guid.NewGuid().ToString("D"));
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/assets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(GatewayHeaderConstants.UserId, "forged-user-id");
        request.Headers.Add(GatewayHeaderConstants.Role, "forged-role");
        request.Headers.Add(GatewayHeaderConstants.OrganizationId, "forged-org-id");
        request.Headers.Add(GatewayHeaderConstants.PartyId, "forged-party-id");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.UserId));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.Role));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.OrganizationId));
        Assert.False(upstream.LastRequest?.Headers.ContainsKey(GatewayHeaderConstants.PartyId));
    }

    [Fact]
    public async Task InventoryOpenApiRouteAllowsAnonymous()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/inventory/swagger/v1/swagger.json");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest?.Path);
    }

    private static WebApplicationFactory<Program> CreateFactory(Uri upstreamAddress)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
                    ["Gateway:Authentication:Jwt:Audience"] = HumanAudience,
                    ["Gateway:Authentication:Jwt:InventoryAudience"] = ServiceAudience,
                    ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, "/.well-known/jwks.json").ToString(),
                    ["Routes:0:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:0:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:1:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:1:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:2:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:2:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:3:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:3:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:4:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:4:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:5:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:5:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:6:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:6:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                    ["Routes:7:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
                    ["Routes:7:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture),
                });
            });
        });
    }

    private static string CreateToken(RSA signingKey, string keyId, string issuer, string audience, string sub)
    {
        RsaSecurityKey securityKey = new(signingKey) { KeyId = keyId };
        SigningCredentials credentials = new(securityKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", sub), new Claim("r", "Member")],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateServiceToken(RSA signingKey, string keyId, string issuer, string audience, string sub)
    {
        RsaSecurityKey securityKey = new(signingKey) { KeyId = keyId };
        SigningCredentials credentials = new(securityKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", sub), new Claim("u", "svc"), new Claim("s", "inventory.allocation.write")],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateJwks(RSA rsa, string keyId)
    {
        RSAParameters parameters = rsa.ExportParameters(false);
        var key = new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = keyId,
            n = Base64UrlEncoder.Encode(parameters.Modulus),
            e = Base64UrlEncoder.Encode(parameters.Exponent)
        };
        return JsonSerializer.Serialize(new { keys = new[] { key } });
    }

    private sealed class LoopbackTestService : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task listenerTask;

        public LoopbackTestService(string jwks)
        {
            Jwks = jwks;
            int port = GetAvailablePort();
            listener = new HttpListener();
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
            listener.Start();
            BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"));
            listenerTask = Task.Run(AcceptRequestsAsync);
        }

        public Uri BaseAddress { get; }
        public string Jwks { get; }
        public ReceivedTestRequest? LastRequest { get; private set; }

        public async ValueTask DisposeAsync()
        {
            cancellationTokenSource.Cancel();
            listener.Stop();
            try { await listenerTask; } catch { }
            listener.Close();
            cancellationTokenSource.Dispose();
        }

        private async Task AcceptRequestsAsync()
        {
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(cancellationTokenSource.Token);
                    _ = HandleRequestAsync(context);
                }
            }
            catch { }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            if (string.Equals(req.Url?.AbsolutePath, "/.well-known/jwks.json", StringComparison.Ordinal))
            {
                await WriteResponseAsync(context.Response, Jwks, "application/json");
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (req.Headers.AllKeys != null)
            {
                foreach (string? name in req.Headers.AllKeys)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        headers[name] = req.Headers[name] ?? string.Empty;
                    }
                }
            }

            LastRequest = new ReceivedTestRequest(
                req.Url?.AbsolutePath ?? string.Empty,
                req.Headers["Authorization"] ?? string.Empty,
                headers);

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

    private sealed record ReceivedTestRequest(
        string Path,
        string Authorization,
        IReadOnlyDictionary<string, string> Headers);
}
