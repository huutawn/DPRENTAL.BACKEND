using System.IO;
using Dprental.AnalyticsService.Api.Exceptions;
using Dprental.AnalyticsService.Api.Security;
using Dprental.AnalyticsService.Api.Background;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Application.Events.Payment;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAnalyticsServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddScoped<PaymentAnalyticsEventIngestor>();
builder.Services.AddScoped<RevenueAnalyticsQueryService>();
builder.Services.AddScoped<RentalEventIngestor>();
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<OrderTrendQueryService>();
builder.Services.Configure<AnalyticsKafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.AddHostedService<AnalyticsKafkaConsumerWorker>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AnalyticsServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddControllers();
builder.Services.AddAnalyticsSecurity(builder.Configuration);
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
/// Điểm vào ứng dụng AnalyticsService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
