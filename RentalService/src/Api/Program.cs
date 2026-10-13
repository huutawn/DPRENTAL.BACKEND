using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.RentalService.Api.Exceptions;
using Dprental.RentalService.Api.Routing;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DependencyInjection;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRentalServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddRentalServiceApplication();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RentalServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddHttpContextAccessor();
builder.Services.AddRentalJwtAuthentication(builder.Configuration);
builder.Services.AddScoped<IRentalCallerContext>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>();
    return RentalCallerContext.FromClaimsPrincipal(http.HttpContext?.User);
});
builder.Services.Configure<RentalTermsOptions>(builder.Configuration.GetSection(RentalTermsOptions.SectionName));
builder.Services.AddScoped<ICurrentRenter, CurrentRenter>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Renter", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => RentalCallerContext.FromClaimsPrincipal(context.User).UserId.HasValue));
});
builder.Services.AddControllers(options =>
    options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)))
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DPRental - RentalService API",
        Version = "v1"
    });

    // The cart alias remains routable; document each operation only at its canonical URL.
    options.DocInclusionPredicate((_, api) =>
        api.RelativePath is not null &&
        api.RelativePath.StartsWith(ApiRoutes.Prefix + "/", StringComparison.Ordinal));

    options.InferSecuritySchemes();

    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }

    var appXml = Path.Combine(AppContext.BaseDirectory, "RentalService.Application.xml");
    if (File.Exists(appXml))
    {
        options.IncludeXmlComments(appXml);
    }

    var domainXml = Path.Combine(AppContext.BaseDirectory, "RentalService.Domain.xml");
    if (File.Exists(domainXml))
    {
        options.IncludeXmlComments(domainXml);
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
/// Điểm vào ứng dụng RentalService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
