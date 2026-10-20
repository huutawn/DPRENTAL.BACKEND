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
using Dprental.MediaService.Domain.Processing;
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
builder.Services.AddSingleton<IImageProcessor, SkiaSharpImageProcessor>();

// 3. Application Services
builder.Services.AddScoped<IMediaUploadService, MediaUploadService>();
builder.Services.AddScoped<IMediaAccessService, MediaAccessService>();
builder.Services.AddScoped<IMediaCleanupService, MediaCleanupService>();
builder.Services.AddScoped<IImageVariantService, ImageVariantService>();
builder.Services.AddScoped<IImageVariantBackfillService, ImageVariantBackfillService>();

bool isBackfillMode = args.Contains("--backfill-image-variants");
if (!isBackfillMode)
{
    builder.Services.AddHostedService<MediaCleanupWorker>();
}
builder.Services.AddOptions<UploadSettings>();
builder.Services.AddOptions<MediaAccessSettings>()
    .Configure<IOptions<ObjectStorageOptions>>((settings, storage) =>
        settings.ReadUrlExpirySeconds = storage.Value.ReadUrlExpirySeconds);

// 4. Security & CORS
builder.Services.AddMediaAuthentication(builder.Configuration);
builder.Services.AddSingleton<IApiKeyValidator>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var key = configuration.GetConfiguredApiKey();
    return new ApiKeyValidator(key ?? string.Empty);
});


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
    options.OperationFilter<ApiKeyOperationFilter>();

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

BackfillOptions? backfillOptions = null;
if (isBackfillMode)
{
    if (!TryParseBackfillOptions(args, out backfillOptions, out var errorMessage))
    {
        Console.Error.WriteLine($"[LỖI THAM SỐ CLI] {errorMessage}");
        Environment.ExitCode = 1;
        return;
    }
}

var app = builder.Build();
if (isBackfillMode && backfillOptions != null)
{
    using var scope = app.Services.CreateScope();
    var backfillService = scope.ServiceProvider.GetRequiredService<IImageVariantBackfillService>();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, eventArgs) =>
    {
        eventArgs.Cancel = true;
        Console.WriteLine("\n[Ctrl+C] Nhận tín hiệu ngắt. Đang dừng an toàn và lưu checkpoint...");
        cts.Cancel();
    };

    var summary = await backfillService.RunBackfillAsync(backfillOptions, cts.Token);

    Console.WriteLine("[BACKFILL SUMMARY]");
    Console.WriteLine($"Total processed: {summary.TotalProcessed}");
    Console.WriteLine($"Succeeded: {summary.SucceededCount}");
    Console.WriteLine($"Skipped: {summary.SkippedCount}");
    Console.WriteLine($"Failed: {summary.FailedCount}");
    if (summary.FailedMediaIds.Count > 0)
    {
        Console.WriteLine($"Failed IDs: {string.Join(", ", summary.FailedMediaIds)}");
    }
    if (summary.LastCheckpointId.HasValue)
    {
        Console.WriteLine($"Checkpoint ID: {summary.LastCheckpointId.Value}");
    }

    Environment.ExitCode = summary.FailedCount > 0 ? 1 : 0;
    return;
}

static bool TryParseBackfillOptions(string[] args, out BackfillOptions? options, out string? errorMessage)
{
    options = null;
    errorMessage = null;

    bool dryRun = false;
    int batchSize = 25;
    Guid? afterId = null;
    Guid? mediaId = null;

    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (string.Equals(arg, "--backfill-image-variants", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
        {
            dryRun = true;
        }
        else if (arg.StartsWith("--batch-size=", StringComparison.OrdinalIgnoreCase))
        {
            string val = arg["--batch-size=".Length..].Trim();
            if (!int.TryParse(val, out int size) || size is < 1 or > 500)
            {
                errorMessage = $"--batch-size không hợp lệ: '{val}'. Phải là số nguyên từ 1 đến 500.";
                return false;
            }
            batchSize = size;
        }
        else if (string.Equals(arg, "--batch-size", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                errorMessage = "Thiếu giá trị cho tham số --batch-size.";
                return false;
            }
            string val = args[++i].Trim();
            if (!int.TryParse(val, out int size) || size is < 1 or > 500)
            {
                errorMessage = $"--batch-size không hợp lệ: '{val}'. Phải là số nguyên từ 1 đến 500.";
                return false;
            }
            batchSize = size;
        }
        else if (arg.StartsWith("--after-id=", StringComparison.OrdinalIgnoreCase))
        {
            string val = arg["--after-id=".Length..].Trim();
            if (!Guid.TryParse(val, out var aid) || aid == Guid.Empty)
            {
                errorMessage = $"--after-id không hợp lệ: '{val}'. Phải là GUID hợp lệ.";
                return false;
            }
            afterId = aid;
        }
        else if (string.Equals(arg, "--after-id", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                errorMessage = "Thiếu giá trị cho tham số --after-id.";
                return false;
            }
            string val = args[++i].Trim();
            if (!Guid.TryParse(val, out var aid) || aid == Guid.Empty)
            {
                errorMessage = $"--after-id không hợp lệ: '{val}'. Phải là GUID hợp lệ.";
                return false;
            }
            afterId = aid;
        }
        else if (arg.StartsWith("--media-id=", StringComparison.OrdinalIgnoreCase))
        {
            string val = arg["--media-id=".Length..].Trim();
            if (!Guid.TryParse(val, out var mid) || mid == Guid.Empty)
            {
                errorMessage = $"--media-id không hợp lệ: '{val}'. Phải là GUID hợp lệ.";
                return false;
            }
            mediaId = mid;
        }
        else if (string.Equals(arg, "--media-id", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                errorMessage = "Thiếu giá trị cho tham số --media-id.";
                return false;
            }
            string val = args[++i].Trim();
            if (!Guid.TryParse(val, out var mid) || mid == Guid.Empty)
            {
                errorMessage = $"--media-id không hợp lệ: '{val}'. Phải là GUID hợp lệ.";
                return false;
            }
            mediaId = mid;
        }
        else
        {
            errorMessage = $"Tùy chọn không được nhận diện: '{arg}'.";
            return false;
        }
    }

    if (mediaId.HasValue && afterId.HasValue)
    {
        errorMessage = "Không thể sử dụng đồng thời cả hai tham số --media-id và --after-id.";
        return false;
    }

    options = new BackfillOptions(dryRun, batchSize, afterId, mediaId);
    return true;
}


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseCors();
app.UseMiddleware<InternalApiKeyMiddleware>();
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
