using System.IO;
using Dprental.ReviewService.Api.Exceptions;
using Dprental.ReviewService.Api.Routing;
using Dprental.ReviewService.Api.Security;
using Dprental.ReviewService.Application.DependencyInjection;
using Dprental.ReviewService.Application.Security;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReviewServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));

builder.Services.AddReviewServiceApplication(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddReviewJwtAuthentication(builder.Configuration);

builder.Services.AddScoped<IReviewCallerContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    return ReviewCallerContext.FromClaimsPrincipal(httpContext?.User);
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ReviewServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddControllers(options =>
    options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)));
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
/// Điểm vào ứng dụng ReviewService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
