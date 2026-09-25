using Dprental.MediaService.Api.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace Dprental.MediaService.Api.Health;

/// <summary>
/// Health check phục vụ probe readiness kiểm tra kết nối MinIO và sự tồn tại của bucket lưu trữ.
/// </summary>
public sealed class MinioHealthCheck : IHealthCheck
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;

    public MinioHealthCheck(IMinioClient minioClient, IOptions<ObjectStorageOptions> options)
    {
        _minioClient = minioClient ?? throw new ArgumentNullException(nameof(minioClient));
        ArgumentNullException.ThrowIfNull(options);
        _bucketName = options.Value.Bucket;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var bucketArgs = new BucketExistsArgs().WithBucket(_bucketName);
            bool exists = await _minioClient.BucketExistsAsync(bucketArgs, timeoutCts.Token);

            if (!exists)
            {
                return HealthCheckResult.Unhealthy(
                    $"Bucket '{_bucketName}' không tồn tại trên MinIO.");
            }

            return HealthCheckResult.Healthy(
                $"MinIO sẵn sàng và bucket '{_bucketName}' tồn tại.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Kiểm tra MinIO thất bại: {ex.Message}", ex);
        }
    }
}
