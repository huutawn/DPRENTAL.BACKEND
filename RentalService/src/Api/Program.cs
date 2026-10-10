using System.IO;
using Dprental.RentalService.Api.Exceptions;
using Dprental.RentalService.Api.Routing;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.IServices;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRentalServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RentalServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRentalJwtAuthentication(builder.Configuration);
builder.Services.AddScoped<IRentalCallerContext>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>();
    return RentalCallerContext.FromClaimsPrincipal(http.HttpContext?.User);
});
builder.Services.AddScoped<IRentalCalendarService, RentalCalendarService>();

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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>
/// Điểm vào ứng dụng RentalService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
