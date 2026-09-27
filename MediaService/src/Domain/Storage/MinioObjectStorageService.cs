using Dprental.MediaService.Domain.IStorage;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Dprental.MediaService.Domain.Storage;

public class MinioObjectStorageService : IObjectStorageService
{
    private readonly IMinioClient _internalClient;
    private readonly IMinioClient _presignClient;
    private readonly string _bucket;

    public MinioObjectStorageService(
        IMinioClient internalClient,
        IMinioClient presignClient,
        string bucket)
    {
        _internalClient = internalClient ?? throw new ArgumentNullException(nameof(internalClient));
        _presignClient = presignClient ?? throw new ArgumentNullException(nameof(presignClient));
        _bucket = string.IsNullOrWhiteSpace(bucket) ? throw new ArgumentException("Bucket không được để trống.", nameof(bucket)) : bucket;
    }

    public async Task<PresignedUploadUrlResult> GeneratePresignedUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var args = new PresignedPutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithExpiry((int)expiry.TotalSeconds);

        string url = await _presignClient.PresignedPutObjectAsync(args);

        var requiredHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            requiredHeaders["Content-Type"] = contentType;
        }

        return new PresignedUploadUrlResult(url, "PUT", requiredHeaders);
    }

    public async Task<ObjectMetadataResult?> GetObjectMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        try
        {
            var stat = await _internalClient.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucket).WithObject(objectKey),
                cancellationToken);

            return new ObjectMetadataResult(stat.Size, stat.ContentType, stat.ETag);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    public async Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        var memoryStream = new MemoryStream();
        try
        {
            await _internalClient.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(objectKey)
                    .WithCallbackStream(async (stream, ct) =>
                    {
                        await stream.CopyToAsync(memoryStream, ct);
                    }),
                cancellationToken);

            memoryStream.Position = 0;
            return memoryStream;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            await memoryStream.DisposeAsync();
            return null;
        }
        catch
        {
            await memoryStream.DisposeAsync();
            throw;
        }
    }

    public async Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationKey);

        var copySource = new CopySourceObjectArgs()
            .WithBucket(_bucket)
            .WithObject(sourceKey);

        await _internalClient.CopyObjectAsync(
            new CopyObjectArgs()
                .WithBucket(_bucket)
                .WithObject(destinationKey)
                .WithCopyObjectSource(copySource),
            cancellationToken);

    }

    public async Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        try
        {
            await _internalClient.RemoveObjectAsync(
                new RemoveObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(objectKey),
                cancellationToken);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            // Nếu object không tồn tại, coi như đã xóa thành công (idempotent)
        }
    }

    private static bool IsNotFound(Exception ex)
    {
        return ex switch
        {
            ObjectNotFoundException => true,
            ErrorResponseException errorEx when errorEx.Response.Code is "NoSuchKey" or "NotFound" => true,
            _ => false
        };
    }
}
