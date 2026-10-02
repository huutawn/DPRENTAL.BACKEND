using Dprental.CatalogService.Api.Exceptions;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Api.Routing;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.DependencyInjection;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCatalogServiceDistributedTracing(builder.Configuration);
builder.Services.AddCatalogServiceApplication(builder.Configuration);
builder.Services.AddCatalogServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddJwtBearerSecurity(builder.Configuration);
builder.Services.AddCatalogAuthorizationPolicies();
builder.Services.AddAuthForwardedHeaders(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICorrelationIdProvider, CorrelationIdProvider>();

string[] configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? [];

if (configuredOrigins.Length == 0 && builder.Environment.IsDevelopment())
{
    configuredOrigins = ["http://localhost:3000"];
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (configuredOrigins.Length > 0)
        {
            policy.WithOrigins(configuredOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

builder.Services.AddControllers(options => options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.InferSecuritySchemes();
    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    string? correlationId = context.Request.Headers[CorrelationIdProvider.HeaderName].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString();
    }

    context.Items[CorrelationIdProvider.ItemKey] = correlationId;
    context.Response.Headers[CorrelationIdProvider.HeaderName] = correlationId;

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

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

app.Run();

/// <summary>
/// Điểm vào ứng dụng CatalogService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
