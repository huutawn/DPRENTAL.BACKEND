using Dprental.MediaService.Api.Configuration;
using Dprental.MediaService.Api.Health;
using Dprental.MediaService.Api.Options;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký startup filter xác thực cấu hình bắt buộc khi ứng dụng khởi động
builder.Services.AddTransient<IStartupFilter, ConfigurationValidationStartupFilter>();

builder.Services.AddOptions<ObjectStorageOptions>()
    .BindConfiguration(ObjectStorageOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();

builder.Services.AddSingleton<IPostgreSqlHealthChecker, DefaultPostgreSqlHealthChecker>();
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
    return MediaMinioClientFactory.Create(options);
});

builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlHealthCheck>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"])
    .AddCheck<MinioHealthCheck>(
        "minio",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFilename = $"{typeof(Program).Assembly.GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
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

app.Run();

/// <summary>
/// Điểm vào ứng dụng MediaService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
