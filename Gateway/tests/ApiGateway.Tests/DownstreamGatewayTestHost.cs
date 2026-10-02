using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.Gateway.Tests;

internal static class DownstreamGatewayTestHost
{
    internal static WebApplicationFactory<Program> CreateFactory(Uri upstreamAddress)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                var configuration = configurationBuilder.Build();
                Dictionary<string, string?> overrides = new()
                {
                    ["Gateway:Authentication:Jwt:Issuer"] = "https://auth.example.test",
                    ["Gateway:Authentication:Jwt:Audience"] = "dprental-clients",
                    ["Gateway:Authentication:Jwt:InventoryAudience"] = "inventory-api",
                    ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, "/.well-known/jwks.json").ToString()
                };
                foreach (var route in configuration.GetSection("Routes").GetChildren())
                {
                    overrides[$"{route.Path}:DownstreamScheme"] = upstreamAddress.Scheme;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture);
                }
                configurationBuilder.AddInMemoryCollection(overrides);
            });
        });
    }

    internal static string CreateToken(RSA signingKey, string keyId, string issuer, string audience, string sub, string role = "Member")
    {
        RsaSecurityKey securityKey = new(signingKey) { KeyId = keyId };
        SigningCredentials credentials = new(securityKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", sub), new Claim("r", role), new Claim("sid", Guid.NewGuid().ToString("D"))],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static string CreateServiceToken(RSA signingKey, string keyId, string issuer, string audience, string sub)
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

    internal static string CreateJwks(RSA rsa, string keyId)
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

    internal sealed class LoopbackTestService : IAsyncDisposable
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
        public int ResponseStatusCode { get; set; } = (int)HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{\"proxied\":true}";
        public string ResponseContentType { get; set; } = "application/json";
        public ReceivedTestRequest? LastRequest { get; private set; }

        public async ValueTask DisposeAsync()
        {
            cancellationTokenSource.Cancel();
            listener.Stop();
            try
            {
                await listenerTask;
            }
            finally
            {
                listener.Close();
                cancellationTokenSource.Dispose();
            }
        }

        private async Task AcceptRequestsAsync()
        {
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(cancellationTokenSource.Token);
                    await HandleRequestAsync(context);
                }
            }
            catch (Exception exception) when (cancellationTokenSource.IsCancellationRequested &&
                exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
            }
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

            using var bodyReader = new System.IO.StreamReader(req.InputStream);
            LastRequest = new ReceivedTestRequest(
                req.Url?.PathAndQuery ?? string.Empty,
                req.Headers["Authorization"] ?? string.Empty,
                headers,
                await bodyReader.ReadToEndAsync(cancellationTokenSource.Token));

            await WriteResponseAsync(context.Response, ResponseBody, ResponseContentType, ResponseStatusCode);
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, string content, string contentType, int statusCode = (int)HttpStatusCode.OK)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            response.StatusCode = statusCode;
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

    internal sealed record ReceivedTestRequest(
        string Path,
        string Authorization,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
