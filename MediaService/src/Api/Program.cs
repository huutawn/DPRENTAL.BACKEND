using Dprental.MediaService.Api.Configuration;
using Dprental.MediaService.Api.Health;
using Dprental.MediaService.Api.Options;
using Dprental.MediaService.Api.Security;
using Dprental.MediaService.Application.IServices;
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
builder.Services.AddScoped<IMediaCleanupService, MediaCleanupService>();

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
        else
        {
            policy.AllowAnyOrigin()
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
    app.MapGet("/test-upload", () => Results.Content(TestUploadHtml, "text/html")).AllowAnonymous();
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
    private const string TestUploadHtml = """
        <!DOCTYPE html>
        <html lang="vi">
        <head>
            <meta charset="UTF-8">
            <title>MediaService - Test Direct Upload</title>
            <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 40px auto; padding: 20px; line-height: 1.5; }
                .card { border: 1px solid #ddd; border-radius: 8px; padding: 24px; box-shadow: 0 2px 4px rgba(0,0,0,0.05); }
                label { display: block; font-weight: bold; margin-top: 12px; margin-bottom: 4px; }
                input, select, button { width: 100%; padding: 8px 12px; border: 1px solid #ccc; border-radius: 4px; box-sizing: border-box; font-size: 14px; }
                button { background-color: #0066cc; color: white; border: none; font-weight: bold; margin-top: 20px; cursor: pointer; padding: 12px; }
                button:hover { background-color: #0052a3; }
                pre { background: #f4f4f4; padding: 12px; border-radius: 4px; overflow-x: auto; font-size: 13px; max-height: 250px; }
                .step { font-weight: bold; color: #0066cc; margin-top: 8px; }
            </style>
        </head>
        <body>
            <div class="card">
                <h2>🚀 Thử nghiệm Tải tệp trực tiếp lên MinIO</h2>
                <label>User ID:</label>
                <input type="text" id="userId" value="test-user-01">

                <label>Purpose (Mục đích):</label>
                <select id="purpose">
                    <option value="equipment-image">equipment-image (JPEG/PNG/WebP, &lt;= 10 MiB)</option>
                    <option value="organization-logo">organization-logo (JPEG/PNG/WebP, &lt;= 10 MiB)</option>
                    <option value="equipment-document">equipment-document (PDF, &lt;= 20 MiB)</option>
                </select>

                <label>Owner Entity ID:</label>
                <input type="text" id="ownerEntityId" value="equipment-test-123">

                <label>Chọn tệp:</label>
                <input type="file" id="fileInput">

                <button type="button" onclick="startDirectUpload()">Bắt đầu Tải tệp (3 bước tự động)</button>

                <div id="statusArea" style="margin-top: 20px; display: none;">
                    <div id="stepInfo" class="step"></div>
                    <pre id="logOutput"></pre>
                </div>
            </div>

            <script>
                async function startDirectUpload() {
                    const fileInput = document.getElementById('fileInput');
                    if (!fileInput.files.length) {
                        alert('Vui lòng chọn một tệp!');
                        return;
                    }
                    const file = fileInput.files[0];
                    const userId = document.getElementById('userId').value.trim();
                    const purpose = document.getElementById('purpose').value;
                    const ownerEntityId = document.getElementById('ownerEntityId').value.trim();

                    const statusArea = document.getElementById('statusArea');
                    const stepInfo = document.getElementById('stepInfo');
                    const logOutput = document.getElementById('logOutput');
                    statusArea.style.display = 'block';
                    logOutput.textContent = '';

                    function log(msg) {
                        logOutput.textContent += msg + '\n';
                    }

                    try {
                        stepInfo.textContent = '1/3: Đang gọi POST /api/v1/media/uploads...';
                        const idempotencyKey = crypto.randomUUID();
                        const initRes = await fetch('/api/v1/media/uploads', {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-User-Id': userId,
                                'Idempotency-Key': idempotencyKey
                            },
                            body: JSON.stringify({
                                purpose: purpose,
                                ownerEntityId: ownerEntityId,
                                fileName: file.name,
                                contentType: file.type || 'application/octet-stream',
                                sizeBytes: file.size
                            })
                        });

                        if (!initRes.ok) {
                            const err = await initRes.text();
                            throw new Error('Khởi tạo thất bại: ' + err);
                        }

                        const session = await initRes.json();
                        log('✅ Bước 1 thành công: mediaId = ' + session.mediaId);
                        log('🔗 Presigned URL = ' + session.uploadUrl);

                        stepInfo.textContent = '2/3: Đang PUT binary trực tiếp lên MinIO...';
                        const putRes = await fetch(session.uploadUrl, {
                            method: 'PUT',
                            headers: {
                                'Content-Type': file.type || 'application/octet-stream'
                            },
                            body: file
                        });

                        if (!putRes.ok) {
                            throw new Error('PUT lên MinIO thất bại, status = ' + putRes.status);
                        }
                        log('✅ Bước 2 thành công: Đã tải binary trực tiếp lên MinIO.');

                        stepInfo.textContent = '3/3: Đang gọi POST /api/v1/media/uploads/{id}/complete...';
                        const completeRes = await fetch('/api/v1/media/uploads/' + session.uploadId + '/complete', {
                            method: 'POST',
                            headers: {
                                'X-User-Id': userId
                            }
                        });

                        if (!completeRes.ok) {
                            const err = await completeRes.text();
                            throw new Error('Complete thất bại: ' + err);
                        }

                        const completed = await completeRes.json();
                        log('🎉 Hoàn tất thành công!');
                        log(JSON.stringify(completed, null, 2));
                        stepInfo.textContent = '✅ Upload thành công! Trạng thái: ' + completed.status;
                    } catch (e) {
                        stepInfo.textContent = '❌ Lỗi: ' + e.message;
                        log('Lỗi chi tiết: ' + e);
                    }
                }
            </script>
        </body>
        </html>
        """;
}
