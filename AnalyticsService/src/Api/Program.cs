using System.IO;
using Dprental.AnalyticsService.Api.Background;
using Dprental.AnalyticsService.Api.Exceptions;
using Dprental.AnalyticsService.Api.Security;
using Dprental.AnalyticsService.Application.DependencyInjection;
using Dprental.AnalyticsService.Application.Hub.Extensions;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Kafka.DependencyInjection;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAnalyticsServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddAnalyticsApplication();
builder.Services.AddAnalyticsJwtAuthentication(builder.Configuration);
builder.Services.AddAnalyticsHub(builder.Configuration);
builder.Services.AddAnalyticsKafka(builder.Configuration);

// Worker chỉ chạy khi bật Kafka tường minh; cấu hình sai đã bị chặn lúc khởi động bởi KafkaOptionsValidator
if (builder.Configuration.GetValue<bool>($"{KafkaOptions.SectionName}:{nameof(KafkaOptions.Enabled)}"))
{
    builder.Services.AddHostedService<AnalyticsKafkaConsumerWorker>();
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AnalyticsServiceDbContext>(
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
app.UseAnalyticsHubOriginGuard();
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
app.MapAnalyticsHub();

app.Run();

/// <summary>
/// Điểm vào ứng dụng AnalyticsService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
