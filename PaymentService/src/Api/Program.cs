using System.IO;
using Dprental.PaymentService.Domain.DependencyInjection;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Clients;
using System.Net;
using Dprental.PaymentService.Api.Exceptions;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.Options;
using System.Text;
using Dprental.PaymentService.Application.DependencyInjection;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Dprental.PaymentService.Application.Hub.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPaymentDomainPersistence(builder.Configuration.GetConnectionString("Postgres")!);
builder.Services.AddPaymentApplication(builder.Configuration);
builder.Services.AddPaymentHub(builder.Configuration);
builder.Services.AddHttpClient<ITransactionIntegrityChecker, IntegrityHostClient>((sp, client) =>
{
    string hostUrl = builder.Configuration["IntegrityHost:BaseUrl"]
        ?? throw new InvalidOperationException("Missing required configuration: 'IntegrityHost:BaseUrl'.");
    client.BaseAddress = new Uri(hostUrl);
    int timeoutMs = builder.Configuration.GetValue<int>("IntegrityVerification:ServerBudgetMilliseconds", 3000);
    client.Timeout = TimeSpan.FromMilliseconds(Math.Max(3000, timeoutMs));
    string internalKey = builder.Configuration["IntegrityHost:InternalApiKey"]
        ?? throw new InvalidOperationException("Missing required configuration: 'IntegrityHost:InternalApiKey'.");
    client.DefaultRequestHeaders.Add("X-Api-Key", internalKey);
});

builder.Services.AddPaymentJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InvoiceServicePolicy", policy =>
    {
        policy.AddAuthenticationSchemes(PaymentServiceApiKeyDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.IsServiceToken() &&
            ctx.User.HasScope("payment.invoice.write"));
    });

    options.AddPolicy("SettlementServicePolicy", policy =>
    {
        policy.AddAuthenticationSchemes(PaymentServiceApiKeyDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.IsServiceToken() &&
            ctx.User.HasScope("payment.settlement.write"));
    });

    options.AddPolicy("RefundServicePolicy", policy =>
    {
        policy.AddAuthenticationSchemes(PaymentServiceApiKeyDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.IsServiceToken() && ctx.User.HasScope("payment.refund.write"));
    });

    options.AddPolicy("InvoiceRenterPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.TryGetSubjectUserId(out _));
    });

    options.AddPolicy("ManualRefundAdminPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.IsPlatformAdmin() &&
            !ctx.User.IsServiceToken() &&
            ctx.User.TryGetSubjectUserId(out _));
    });

    options.AddPolicy(PayoutPolicies.Admin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => !ctx.User.IsServiceToken() &&
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.IsPlatformAdmin() && ctx.User.TryGetSubjectUserId(out _));
    });

    options.AddPolicy("FinancialReadClientPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => !ctx.User.IsServiceToken() &&
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.TryGetSubjectUserId(out var id) && id != Guid.Empty);
    });

    options.AddPolicy("FinancialReadAdminPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => !ctx.User.IsServiceToken() &&
            ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
            ctx.User.IsPlatformAdmin() && ctx.User.TryGetSubjectUserId(out var id) && id != Guid.Empty);
    });

    options.AddPolicy("TransactionVerifyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes(PaymentServiceApiKeyDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            ctx.User.IsServiceToken() &&
            ctx.User.HasScope("payment.transaction.verify"));
    });

    options.AddPolicy("TransactionHistoryPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
            (ctx.User.IsServiceToken() &&
             ctx.User.HasServiceAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ServiceAudience") ?? "payment-api") &&
             ctx.User.HasScope("payment.transaction.history")) ||
            (ctx.User.HasClientAudience(builder.Configuration.GetValue<string>("Authentication:Jwt:ClientAudience") ?? "dprental-clients") &&
             ctx.User.IsPlatformAdmin() &&
             !ctx.User.IsServiceToken()));
    });
});

string integrityApiKey = builder.Configuration["IntegrityHost:InternalApiKey"] ?? string.Empty;
if (Encoding.UTF8.GetByteCount(integrityApiKey) < 32)
{
    throw new InvalidOperationException("IntegrityHost:InternalApiKey must be configured with at least 32 bytes.");
}

var forwardedSection = builder.Configuration.GetSection(ForwardedHeadersSecurityOptions.SectionName);
var rawProxies = forwardedSection.GetSection("KnownProxies").Get<List<string>>() ?? [];
var rawNetworks = forwardedSection.GetSection("KnownNetworks").Get<List<string>>() ?? [];

bool hasProxyConfig = rawProxies.Count > 0 || rawNetworks.Count > 0;

if (builder.Environment.IsProduction())
{
    if (!hasProxyConfig)
    {
        throw new InvalidOperationException(
            "Production environment requires reverse proxy trust configuration in Security:ForwardedHeaders (KnownProxies or KnownNetworks).");
    }
}

List<IPAddress> validProxies = [];
List<System.Net.IPNetwork> validNetworks = [];

if (hasProxyConfig)
{
    foreach (var proxyStr in rawProxies)
    {
        if (string.IsNullOrWhiteSpace(proxyStr) || !IPAddress.TryParse(proxyStr.Trim(), out var ip))
        {
            throw new InvalidOperationException($"Invalid IP address in Security:ForwardedHeaders:KnownProxies: '{proxyStr}'.");
        }

        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
        {
            throw new InvalidOperationException($"Security:ForwardedHeaders:KnownProxies cannot be a wildcard address ('{proxyStr}').");
        }

        validProxies.Add(ip);
    }

    foreach (var networkStr in rawNetworks)
    {
        if (string.IsNullOrWhiteSpace(networkStr) || !System.Net.IPNetwork.TryParse(networkStr.Trim(), out var network))
        {
            throw new InvalidOperationException($"Invalid CIDR network in Security:ForwardedHeaders:KnownNetworks: '{networkStr}'.");
        }

        if (network.PrefixLength == 0)
        {
            throw new InvalidOperationException($"Security:ForwardedHeaders:KnownNetworks cannot trust the entire internet ('{networkStr}').");
        }

        validNetworks.Add(network);
    }

    if (validProxies.Count == 0 && validNetworks.Count == 0)
    {
        throw new InvalidOperationException("Security:ForwardedHeaders must contain at least one valid trusted proxy IP or CIDR network.");
    }

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var p in validProxies)
        {
            options.KnownProxies.Add(p);
        }

        foreach (var n in validNetworks)
        {
            options.KnownIPNetworks.Add(n);
        }
    });
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<PaymentServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (hasProxyConfig)
{
    app.UseForwardedHeaders();
}

app.UseAuthentication();
app.UseAuthorization();
app.UsePaymentHubOriginGuard();
HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/live", livenessOptions).AllowAnonymous();

HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains("ready", StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/ready", readinessOptions).AllowAnonymous();

app.MapControllers();
app.MapPaymentHub();

// Eager validation of encryption key at startup
using (var scope = app.Services.CreateScope())
{
    _ = scope.ServiceProvider.GetRequiredService<Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor>();
    _ = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RentalServiceApiKeyOptions>>().Value;
}

app.Run();

/// <summary>
/// Điểm vào ứng dụng PaymentService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
