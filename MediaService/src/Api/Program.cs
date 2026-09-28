using Dprental.MediaService.Api.Configuration;
using Dprental.MediaService.Application.Background;
using Dprental.MediaService.Api.Health;
using Dprental.MediaService.Api.Options;
using Dprental.MediaService.Api.Security;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Application.Services;
using Dprental.MediaService.Domain;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Dprental.MediaService.Domain.Repositories;
using Dprental.MediaService.Domain.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
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

// 1. Persistence & Database
builder.Services.AddDbContext<MediaDbContext>((sp, options) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    string? connectionString = configuration.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", MediaDbContext.SchemaName);
        });
    }
});

builder.Services.AddScoped<IMediaAssetRepository, MediaAssetRepository>();
builder.Services.AddScoped<IMediaUploadSessionRepository, MediaUploadSessionRepository>();
builder.Services.AddScoped<IMediaCleanupRepository, MediaCleanupRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// 2. Object Storage
builder.Services.AddSingleton<IPostgreSqlHealthChecker, DefaultPostgreSqlHealthChecker>();
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
    return MediaMinioClientFactory.Create(options);
});

builder.Services.AddSingleton<IObjectStorageService>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
    var internalClient = sp.GetRequiredService<IMinioClient>();

    IMinioClient presignClient = internalClient;
    if (!string.IsNullOrWhiteSpace(options.BrowserEndpoint) &&
        !string.Equals(options.BrowserEndpoint, options.Endpoint, StringComparison.OrdinalIgnoreCase))
    {
        var browserOptions = new ObjectStorageOptions
        {
            Endpoint = options.BrowserEndpoint,
            Bucket = options.Bucket,
            AccessKey = options.AccessKey,
            SecretKey = options.SecretKey,
            UseSsl = options.BrowserEndpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };
        presignClient = MediaMinioClientFactory.Create(browserOptions);
    }

    return new MinioObjectStorageService(internalClient, presignClient, options.Bucket);
});

// 3. Application Services
builder.Services.AddScoped<IMediaUploadService, MediaUploadService>();
builder.Services.AddScoped<IMediaAccessService, MediaAccessService>();
builder.Services.AddScoped<IMediaCleanupService, MediaCleanupService>();
builder.Services.AddHostedService<MediaCleanupWorker>();
builder.Services.AddOptions<UploadSettings>();
builder.Services.AddOptions<MediaAccessSettings>()
    .Configure<IOptions<ObjectStorageOptions>>((settings, storage) =>
        settings.ReadUrlExpirySeconds = storage.Value.ReadUrlExpirySeconds);

// 4. Security & CORS
builder.Services.AddMediaAuthentication(builder.Configuration);

var storageOptions = builder.Configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>();
builder.Services.AddCors(cors =>
{
    cors.AddDefaultPolicy(policy =>
    {
        if (storageOptions?.AllowedOrigins is { Length: > 0 } origins)
        {
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
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

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSwaggerGen(options =>
{
    options.InferSecuritySchemes();

    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }

    var appXml = Path.Combine(AppContext.BaseDirectory, "MediaService.Application.xml");
    if (File.Exists(appXml))
    {
        options.IncludeXmlComments(appXml);
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
/// Điểm vào ứng dụng MediaService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
