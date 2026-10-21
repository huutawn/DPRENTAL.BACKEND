using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Controllers;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PaymentWebhooksProxyHttpTests : IAsyncLifetime
{
    private const string ApiKey = "prod-sepay-secret-key-99999";
    private const string TrustedProxyIp = "10.0.0.50";
    private const string UntrustedIp = "198.51.100.22";
    private const string TrustedCidr = "172.16.0.0/16";

    private string _connString = string.Empty;
    private string _dbName = string.Empty;
    private readonly TestLogSink _logSink = new();

    public async Task InitializeAsync()
    {
        (_connString, _dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_dbName);
    }

    private WebApplicationFactory<Program> CreateProductionFactory(
        List<string>? knownProxies = null,
        List<string>? knownNetworks = null)
    {
        return new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Payment:SePay:ApiKey", ApiKey);
                builder.UseSetting("Payment:SePay:Gateway", "MBBank");
                builder.UseSetting("Payment:SePay:AccountNumber", "0383799632");
                builder.UseSetting("InternalAuthentication:RentalApiKey", "dev-internal-api-key-dprental-2026");
                var proxies = knownProxies ?? [TrustedProxyIp];
                for (int i = 0; i < proxies.Count; i++)
                {
                    builder.UseSetting($"Security:ForwardedHeaders:KnownProxies:{i}", proxies[i]);
                }

                var networks = knownNetworks ?? [TrustedCidr];
                for (int i = 0; i < networks.Count; i++)
                {
                    builder.UseSetting($"Security:ForwardedHeaders:KnownNetworks:{i}", networks[i]);
                }

                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new TestLoggerProvider(_logSink));
                });
            });
    }

    private static HttpClient CreateClientWithRemoteIp(
        WebApplicationFactory<Program> factory,
        string remoteIp,
        Action<IServiceCollection>? configureServices = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                configureServices?.Invoke(services);
            });

            builder.Configure(app =>
            {
                // Let the standard pipeline execute, but inject remote IP right before forwarded headers
            });
        }).CreateDefaultClient(new SetRemoteIpHandler(IPAddress.Parse(remoteIp)));
    }

    private sealed class SetRemoteIpHandler(IPAddress remoteIp) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // TestServer allows setting remote IP via HttpContext
            request.Headers.Add("X-Test-Set-Remote-Ip", remoteIp.ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task TrustedProxy_ProtoHttps_ValidKey_Returns200_AndProcessorCalled()
    {
        using var factory = CreateProductionFactory();
        bool processorCalled = false;

        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
            });
        }).CreateClient();

        // Inject remote IP using test middleware
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        req.Headers.Add("Authorization", $"Apikey {ApiKey}");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-For", "203.0.113.195");
        // Simulate request arriving from trusted proxy IP
        factory.Server.AllowSynchronousIO = true;

        var clientWithIp = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
            });
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IStartupFilter>(new InjectRemoteIpFilter(IPAddress.Parse(TrustedProxyIp)));
            });
        }).CreateClient();

        var res = await clientWithIp.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(processorCalled);
    }

    [Fact]
    public async Task TrustedProxy_ProtoHttp_Returns401_ProcessorNotCalled()
    {
        using var factory = CreateProductionFactory();
        bool processorCalled = false;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        req.Headers.Add("Authorization", $"Apikey {ApiKey}");
        req.Headers.Add("X-Forwarded-Proto", "http");
        req.Headers.Add("X-Forwarded-For", "203.0.113.195");

        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
                services.AddSingleton<IStartupFilter>(new InjectRemoteIpFilter(IPAddress.Parse(TrustedProxyIp)));
            });
        }).CreateClient();

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(processorCalled);
    }

    [Fact]
    public async Task UntrustedIp_SelfSentProtoHttps_Returns401_ProcessorNotCalled()
    {
        using var factory = CreateProductionFactory();
        bool processorCalled = false;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        req.Headers.Add("Authorization", $"Apikey {ApiKey}");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-For", "203.0.113.195");

        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoicePaymentProcessor>();
                services.AddScoped<IInvoicePaymentProcessor>(_ => new CallbackProcessor(_ =>
                {
                    processorCalled = true;
                    return Task.FromResult(Result.Success());
                }));
                services.AddSingleton<IStartupFilter>(new InjectRemoteIpFilter(IPAddress.Parse(UntrustedIp)));
            });
        }).CreateClient();

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(processorCalled);
    }

    [Fact]
    public async Task Http_WithoutForwardedHeaders_Returns401()
    {
        using var factory = CreateProductionFactory();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        req.Headers.Add("Authorization", $"Apikey {ApiKey}");

        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new InjectRemoteIpFilter(IPAddress.Parse(TrustedProxyIp)));
            });
        }).CreateClient();

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task DirectHttps_ValidKey_IsAuthenticated()
    {
        using var factory = CreateProductionFactory();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("Authorization", $"Apikey {ApiKey}");

        var res = await client.PostAsJsonAsync("/api/v1/payment/webhooks", CreateValidPayload());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task DirectHttps_MissingOrWrongKeyOrBearer_Returns401()
    {
        using var factory = CreateProductionFactory();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        // 1. Missing key
        var res1 = await client.PostAsJsonAsync("/api/v1/payment/webhooks", CreateValidPayload());
        Assert.Equal(HttpStatusCode.Unauthorized, res1.StatusCode);

        // 2. Wrong key
        var reqWrong = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        reqWrong.Headers.Add("Authorization", "Apikey wrong-key-999");
        var res2 = await client.SendAsync(reqWrong);
        Assert.Equal(HttpStatusCode.Unauthorized, res2.StatusCode);

        // 3. Bearer token
        var reqBearer = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payment/webhooks")
        {
            Content = JsonContent.Create(CreateValidPayload())
        };
        reqBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "some-jwt");
        var res3 = await client.SendAsync(reqBearer);
        Assert.Equal(HttpStatusCode.Unauthorized, res3.StatusCode);
    }

    [Theory]
    [InlineData("MissingConfig")]
    [InlineData("InvalidIp")]
    [InlineData("InvalidCidr")]
    [InlineData("EntireInternetV4")]
    [InlineData("EntireInternetV6")]
    public void Production_ProxyConfigMissingOrInvalid_FailsStartup(string scenario)
    {
        var factory = new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Payment:SePay:ApiKey", ApiKey);
                builder.UseSetting("Payment:SePay:Gateway", "MBBank");
                builder.UseSetting("Payment:SePay:AccountNumber", "0383799632");

                switch (scenario)
                {
                    case "MissingConfig":
                        // No KnownProxies or KnownNetworks configured in Production
                        break;
                    case "InvalidIp":
                        builder.UseSetting("Security:ForwardedHeaders:KnownProxies:0", "not-an-ip-address");
                        break;
                    case "InvalidCidr":
                        builder.UseSetting("Security:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/not-a-number");
                        break;
                    case "EntireInternetV4":
                        builder.UseSetting("Security:ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0");
                        break;
                    case "EntireInternetV6":
                        builder.UseSetting("Security:ForwardedHeaders:KnownNetworks:0", "::/0");
                        break;
                }
            });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public async Task JwtInvoiceEndpoint_WorksNormally_WithProxyForwardedHeadersEnabled()
    {
        using var rsa = RSA.Create(2048);
        const string keyId = "jwt-test-key";
        const string issuer = "dprental-authservice-dev";
        const string serviceAudience = "payment-api";

        var factory = new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Payment:SePay:ApiKey", ApiKey);
                builder.UseSetting("Payment:SePay:Gateway", "MBBank");
                builder.UseSetting("Payment:SePay:AccountNumber", "0383799632");
                builder.UseSetting("Security:ForwardedHeaders:KnownProxies:0", TrustedProxyIp);
                builder.UseSetting("Authentication:Jwt:Issuer", issuer);
                builder.UseSetting("Authentication:Jwt:ServiceAudience", serviceAudience);
                builder.UseSetting("InternalAuthentication:RentalApiKey", "dev-internal-api-key-dprental-2026");
                builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                        Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                        options =>
                        {
                            var config = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = issuer };
                            config.SigningKeys.Add(new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = keyId });
                            options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(config);
                        });
                });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = keyId };
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: serviceAudience,
            claims:
            [
                new System.Security.Claims.Claim("u", "svc"),
                new System.Security.Claims.Claim("s", "payment.invoice.write")
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
        string jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var req = new Dprental.PaymentService.Application.DTOs.Invoice.CreateInvoiceRequest(
            Guid.NewGuid(), "ORD-JWT-PROXY", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new Dprental.PaymentService.Application.DTOs.Invoice.CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
            "op-jwt-proxy");

        var res = await client.PostAsJsonAsync("/api/v1/invoices", req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        // Verify logs do not leak credentials
        Assert.DoesNotContain(ApiKey, _logSink.GetAllLogs(), StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, _logSink.GetAllLogs(), StringComparison.Ordinal);
    }

    private static SePayWebhookPayload CreateValidPayload() => new(
        Id: 99001,
        Gateway: "MBBank",
        TransactionDate: "2024-07-02 11:08:33",
        AccountNumber: "0383799632",
        SubAccount: null,
        Code: "DPR 23456789AB",
        Content: "DPR 23456789AB chuyen tien",
        TransferType: "in",
        Description: "Payment proxy test",
        TransferAmount: 500_000m,
        Accumulated: 10_000_000m,
        ReferenceCode: "FT99001");

    private sealed class CallbackProcessor(Func<SePayWebhookPayload?, Task<Result>> callback) : IInvoicePaymentProcessor
    {
        public Task<Result> ProcessAsync(SePayWebhookPayload? payload, CancellationToken ct = default) =>
            callback(payload);
    }

    private sealed class InjectRemoteIpFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(nextMiddleware => async context =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }

    private sealed class TestLogSink
    {
        private readonly List<string> _messages = [];
        private readonly object _lock = new();

        public void Add(string message)
        {
            lock (_lock) _messages.Add(message);
        }

        public string GetAllLogs()
        {
            lock (_lock) return string.Join("\n", _messages);
        }
    }

    private sealed class TestLoggerProvider(TestLogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestLogger(sink);
        public void Dispose() { }

        private sealed class TestLogger(TestLogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
