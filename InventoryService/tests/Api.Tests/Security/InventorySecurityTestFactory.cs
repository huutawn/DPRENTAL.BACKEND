using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Security;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.InventoryService.Api.Tests.Security;

public sealed class InventorySecurityTestFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string Issuer = "https://auth.dprental.test";
    public const string HumanAudience = "dprental-clients";
    public const string ServiceAudience = "inventory-api";

    public Dictionary<Guid, InventoryAsset> TestAssets { get; } = new();

    public RSA SigningKey { get; } = RSA.Create(2048);
    public string ActiveKeyId { get; set; } = "key-active-1";

    private readonly HttpListener _jwksListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;
    public Uri JwksUri { get; }
    public int JwksRequestCount { get; private set; }

    public InventorySecurityTestFactory()
    {
        int port = GetAvailablePort();
        _jwksListener = new HttpListener();
        _jwksListener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _jwksListener.Start();
        JwksUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/.well-known/jwks.json"));
        _listenerTask = Task.Run(AcceptJwksRequestsAsync);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=test;Password=test",
                ["Authentication:Jwt:Issuer"] = Issuer,
                ["Authentication:Jwt:HumanAudience"] = HumanAudience,
                ["Authentication:Jwt:ServiceAudience"] = ServiceAudience,
                ["Authentication:Jwt:JwksUri"] = JwksUri.ToString(),
                ["Authentication:Jwt:TimeoutSeconds"] = "5",
                ["Authentication:Jwt:CacheDurationMinutes"] = "5",
                ["Authentication:Jwt:MaxStaleMinutes"] = "10"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Thay thế DbContext bằng InMemory để chạy unit test authentication / authorization không cần Postgres
            services.RemoveAll<DbContextOptions<InventoryServiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<InventoryServiceDbContext>>();
            services.AddDbContext<InventoryServiceDbContext>(options =>
                options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

            services.AddSingleton<IReadOnlyDictionary<Guid, InventoryAsset>>(TestAssets);
            // Thêm PolicyProbeController vào Mvc builder
            services.AddControllers().AddApplicationPart(typeof(PolicyProbeController).Assembly);
        });
    }

    public string CreateToken(
        string sub,
        IEnumerable<Claim>? claims = null,
        string? audience = HumanAudience,
        string? issuer = Issuer,
        RSA? customKey = null,
        string? keyId = null,
        DateTimeOffset? expiresAt = null)
    {
        var keyToUse = customKey ?? SigningKey;
        var kid = keyId ?? ActiveKeyId;
        var rsaKey = new RsaSecurityKey(keyToUse) { KeyId = kid };
        var credentials = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256);

        var claimList = new List<Claim> { new("sub", sub) };
        if (claims != null)
        {
            claimList.AddRange(claims);
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? HumanAudience,
            claims: claimList,
            expires: (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateHumanToken(
        Guid userId,
        string[]? permissions = null,
        string[]? roles = null,
        Guid? orgId = null)
    {
        var claims = new List<Claim>();
        if (permissions != null)
        {
            claims.AddRange(permissions.Select(p => new Claim("p", p)));
        }

        if (roles != null)
        {
            claims.AddRange(roles.Select(r => new Claim("r", r)));
        }

        if (orgId.HasValue)
        {
            claims.Add(new Claim("o", orgId.Value.ToString("D")));
        }

        return CreateToken(userId.ToString("D"), claims, HumanAudience);
    }

    public string CreateAdminToken(Guid userId)
    {
        return CreateHumanToken(userId, permissions: null, roles: ["adm"]);
    }

    public string CreateServiceToken(
        Guid serviceId,
        string[]? scopes = null,
        string[]? roles = null)
    {
        var claims = new List<Claim> { new("u", "svc") };
        if (scopes != null)
        {
            claims.AddRange(scopes.Select(s => new Claim("s", s)));
        }

        if (roles != null)
        {
            claims.AddRange(roles.Select(r => new Claim("r", r)));
        }

        return CreateToken(serviceId.ToString("D"), claims, ServiceAudience);
    }

    public string GetJwksDocument()
    {
        RSAParameters parameters = SigningKey.ExportParameters(false);
        var key = new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = ActiveKeyId,
            n = Base64UrlEncoder.Encode(parameters.Modulus),
            e = Base64UrlEncoder.Encode(parameters.Exponent)
        };
        return JsonSerializer.Serialize(new { keys = new[] { key } });
    }

    private async Task AcceptJwksRequestsAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var context = await _jwksListener.GetContextAsync().WaitAsync(_cts.Token);
                JwksRequestCount++;
                var response = context.Response;
                byte[] bytes = Encoding.UTF8.GetBytes(GetJwksDocument());
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
        }
        catch { }
    }

    public new async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _jwksListener.Stop();
        try { await _listenerTask; } catch { }
        _jwksListener.Close();
        _cts.Dispose();
        SigningKey.Dispose();
        await base.DisposeAsync();
    }

    private static int GetAvailablePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
