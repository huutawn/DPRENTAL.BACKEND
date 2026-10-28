extern alias AnalyticsApi;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.AnalyticsService.Application.Hub;
using Dprental.AnalyticsService.Application.Hub.Contracts;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;
using AnalyticsProgram = AnalyticsApi::Program;

namespace Dprental.Gateway.Tests;

public sealed class AnalyticsRealtimeGatewayEndToEndTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";
    private const string InternalApiKey = "dev-internal-api-key-dprental-2026";
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private static async Task<(string ConnectionString, string DatabaseName)> CreatePostgreSqlDatabaseAsync()
    {
        string dbName = $"gateway_analytics_e2e_{Guid.NewGuid():N}";
        string baseConn = "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!";

        await using var masterConn = new NpgsqlConnection(baseConn);
        try
        {
            await masterConn.OpenAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot connect to PostgreSQL at {baseConn}: {ex.Message}. Integration environment required.", ex);
        }

        await using var createCmd = masterConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE {dbName};";
        await createCmd.ExecuteNonQueryAsync();

        string connString = $"Host=127.0.0.1;Port=5433;Database={dbName};Username=inventoryservice;Password=inventory_test_secret_123!";
        return (connString, dbName);
    }

    private static async Task DropPostgreSqlDatabaseAsync(string dbName)
    {
        string baseConn = "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!";
        await using var masterConn = new NpgsqlConnection(baseConn);
        await masterConn.OpenAsync();
        NpgsqlConnection.ClearAllPools();
        await using var dropCmd = masterConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
        await dropCmd.ExecuteNonQueryAsync();
    }

    public sealed class KestrelAnalyticsServer : WebApplicationFactory<AnalyticsProgram>
    {
        private readonly string _connectionString;
        private readonly RSA _rsa;
        private readonly string _keyId;

        public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');
        public IServiceProvider AnalyticsServices => Services;

        public KestrelAnalyticsServer(string connectionString, RSA rsa, string keyId)
        {
            _connectionString = connectionString;
            _rsa = rsa;
            _keyId = keyId;
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
            builder.UseSetting("Authentication:Jwt:Audience", Audience);
            builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/test-jwks");
            builder.UseSetting("Kafka:Enabled", "false");
            builder.UseSetting("AnalyticsHub:AllowedOrigins:0", "http://localhost:3000");

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = Audience;
                    options.ConfigurationManager = new TestJwksConfigManager(_rsa, _keyId);
                });
            });
        }

        private sealed class TestJwksConfigManager(RSA rsa, string keyId) : IConfigurationManager<OpenIdConnectConfiguration>
        {
            private readonly OpenIdConnectConfiguration _config = Create(rsa, keyId);
            public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_config);
            public void RequestRefresh() { }
            private static OpenIdConnectConfiguration Create(RSA rsa, string keyId)
            {
                var c = new OpenIdConnectConfiguration();
                c.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
                return c;
            }
        }
    }

    public sealed class KestrelGatewayServer : WebApplicationFactory<Program>
    {
        private readonly Uri _analyticsAddress;
        private readonly string _jwksUri;

        public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

        public KestrelGatewayServer(Uri analyticsAddress, string jwksUri)
        {
            _analyticsAddress = analyticsAddress;
            _jwksUri = jwksUri;
            UseKestrel(0);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var config = configBuilder.Build();
                Dictionary<string, string?> overrides = new()
                {
                    ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
                    ["Gateway:Authentication:Jwt:Audience"] = Audience,
                    ["Gateway:Authentication:Jwt:InventoryAudience"] = "inventory-api",
                    ["Gateway:Authentication:Jwt:JwksUri"] = _jwksUri,
                    ["InternalAuthentication:ApiKey"] = InternalApiKey,
                    ["Gateway:Security:Cors:AllowedOrigins:0"] = "http://localhost:3000"
                };

                foreach (var route in config.GetSection("Routes").GetChildren())
                {
                    string key = route["Key"] ?? "";
                    if (key.StartsWith("analytics", StringComparison.OrdinalIgnoreCase))
                    {
                        string existingScheme = route["DownstreamScheme"] ?? "http";
                        overrides[$"{route.Path}:DownstreamScheme"] = string.Equals(existingScheme, "ws", StringComparison.OrdinalIgnoreCase)
                            ? "ws"
                            : _analyticsAddress.Scheme;
                        overrides[$"{route.Path}:DownstreamHostAndPorts:0:Host"] = _analyticsAddress.Host;
                        overrides[$"{route.Path}:DownstreamHostAndPorts:0:Port"] = _analyticsAddress.Port.ToString(CultureInfo.InvariantCulture);
                    }
                }
                configBuilder.AddInMemoryCollection(overrides);
            });
        }
    }

    private static string CreateJwtToken(RSA signingKey, string keyId, Guid userId, string role = "supplier", bool isService = false, bool expired = false)
    {
        var securityKey = new RsaSecurityKey(signingKey) { KeyId = keyId };
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        List<Claim> claims = [new Claim("sub", userId.ToString("D")), new Claim("r", role), new Claim("sid", Guid.NewGuid().ToString("D"))];
        if (isService) claims.Add(new Claim("u", "svc"));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(30),
            notBefore: expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddSeconds(-5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HubConnection CreateGatewayHubConnection(string gatewayBaseUrl, string? cookieToken = null, string? bearerToken = null, string? origin = "http://localhost:3000")
    {
        var hubUrl = $"{gatewayBaseUrl}/hubs/analytics";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(cookieToken))
                {
                    options.Headers["Cookie"] = $"accessToken={cookieToken}; refreshToken=secret-refresh-token";
                }
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    options.Headers["Authorization"] = $"Bearer {bearerToken}";
                }
                if (!string.IsNullOrWhiteSpace(origin))
                {
                    options.Headers["Origin"] = origin;
                }
            });

        return builder.Build();
    }

    [Fact]
    public async Task RealtimeNotificationDistributionIsolatesAdminAndSuppliers()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<AnalyticsServiceDbContext>().UseNpgsql(connString).Options;
            await using (var db = new AnalyticsServiceDbContext(dbOptions))
            {
                await db.Database.MigrateAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-analytics-key-01";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var analyticsServer = new KestrelAnalyticsServer(connString, rsa, keyId);
            _ = analyticsServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(analyticsServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();

            Guid adminId = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();

            string adminToken = CreateJwtToken(rsa, keyId, adminId, role: "adm");
            string supplierAToken = CreateJwtToken(rsa, keyId, supplierA, role: "supplier");
            string supplierBToken = CreateJwtToken(rsa, keyId, supplierB, role: "supplier");

            var adminConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: adminToken);
            var supplierAConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: supplierAToken);
            var supplierBConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: supplierBToken);

            var adminSignal = new TaskCompletionSource<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierASignal = new TaskCompletionSource<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplierBSignal = new TaskCompletionSource<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(TaskCreationOptions.RunContinuationsAsynchronously);

            adminConn.On<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(AnalyticsHubEventNames.DataChanged, e => adminSignal.TrySetResult(e));
            supplierAConn.On<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(AnalyticsHubEventNames.DataChanged, e => supplierASignal.TrySetResult(e));
            supplierBConn.On<AnalyticsHubEnvelope<AnalyticsDataChangedData>>(AnalyticsHubEventNames.DataChanged, e => supplierBSignal.TrySetResult(e));

            await adminConn.StartAsync();
            await supplierAConn.StartAsync();
            await supplierBConn.StartAsync();

            // Trigger notification specifically targeting Supplier A and Admins
            using (var scope = analyticsServer.AnalyticsServices.CreateScope())
            {
                var notifier = scope.ServiceProvider.GetRequiredService<IAnalyticsHubNotifier>();
                var notifyRes = await notifier.NotifyDataChangedAsync(AnalyticsDataScopes.Finance, [supplierA]);
                Assert.True(notifyRes.IsSuccess);
            }

            // 1. Admin must receive notification
            var adminEnv = await adminSignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(AnalyticsDataScopes.Finance, adminEnv.Data.Scope);

            // 2. Supplier A must receive notification
            var supplierAEnv = await supplierASignal.Task.WaitAsync(ReceiveTimeout);
            Assert.Equal(AnalyticsDataScopes.Finance, supplierAEnv.Data.Scope);
            Assert.Equal(adminEnv.EventId, supplierAEnv.EventId);

            // 3. Supplier B must NOT receive notification (No data leakage!)
            await Task.Delay(300);
            Assert.False(supplierBSignal.Task.IsCompleted, "Supplier B must NOT receive notification meant for Supplier A!");

            await adminConn.StopAsync();
            await supplierAConn.StopAsync();
            await supplierBConn.StopAsync();
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task NegotiateAndWebSocketUpgradeThroughGatewayWithCookieAndBearer()
    {
        var (connString, dbName) = await CreatePostgreSqlDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<AnalyticsServiceDbContext>().UseNpgsql(connString).Options;
            await using (var db = new AnalyticsServiceDbContext(dbOptions))
            {
                await db.Database.MigrateAsync();
            }

            using var rsa = RSA.Create(2048);
            string keyId = "gw-analytics-key-02";
            await using var jwksUpstream = new LoopbackTestService(CreateJwks(rsa, keyId));
            await using var analyticsServer = new KestrelAnalyticsServer(connString, rsa, keyId);
            _ = analyticsServer.CreateClient();
            await using var gatewayServer = new KestrelGatewayServer(new Uri(analyticsServer.ServerAddress), new Uri(jwksUpstream.BaseAddress, "/.well-known/jwks.json").ToString());
            _ = gatewayServer.CreateClient();

            Guid supplierId = Guid.NewGuid();
            string validToken = CreateJwtToken(rsa, keyId, supplierId);
            string serviceToken = CreateJwtToken(rsa, keyId, supplierId, isService: true);

            // 1. Valid Cookie -> Connects successfully!
            var cookieConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: validToken);
            await cookieConn.StartAsync();
            Assert.Equal(HubConnectionState.Connected, cookieConn.State);
            await cookieConn.StopAsync();

            // 2. Valid Bearer Header -> Connects successfully!
            var bearerConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, bearerToken: validToken);
            await bearerConn.StartAsync();
            Assert.Equal(HubConnectionState.Connected, bearerConn.State);
            await bearerConn.StopAsync();

            // 3. Service Token -> 403 Forbidden!
            var serviceConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: serviceToken);
            var exService = await Assert.ThrowsAnyAsync<HttpRequestException>(async () => await serviceConn.StartAsync());
            Assert.Equal(HttpStatusCode.Forbidden, exService.StatusCode);

            // 4. Anonymous (no token) -> 401 Unauthorized!
            var anonConn = CreateGatewayHubConnection(gatewayServer.ServerAddress);
            var exAnon = await Assert.ThrowsAnyAsync<HttpRequestException>(async () => await anonConn.StartAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, exAnon.StatusCode);

            // 5. Disallowed Origin -> 403 Forbidden!
            var corsConn = CreateGatewayHubConnection(gatewayServer.ServerAddress, cookieToken: validToken, origin: "http://disallowed-attacker.com");
            var exCors = await Assert.ThrowsAnyAsync<HttpRequestException>(async () => await corsConn.StartAsync());
            Assert.Equal(HttpStatusCode.Forbidden, exCors.StatusCode);
        }
        finally
        {
            await DropPostgreSqlDatabaseAsync(dbName);
        }
    }
}
