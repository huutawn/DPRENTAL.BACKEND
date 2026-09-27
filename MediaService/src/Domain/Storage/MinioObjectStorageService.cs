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

    public async Task PutObjectAsync(
        string objectKey,
        Stream content,
        long sizeBytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);

        var args = new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(sizeBytes)
            .WithContentType(contentType);

        await _internalClient.PutObjectAsync(args, cancellationToken);
    }

    public async Task<string> GeneratePresignedReadUrlAsync(
        string objectKey,
        string contentType,
        string fileName,
        bool download,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (expiry.TotalSeconds is < 1 or > 900)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string safeFileName = new(fileName
            .Where(c => !char.IsControl(c) && c != '"' && c != '\\')
            .Take(255).ToArray());
        string asciiFileName = new(safeFileName
            .Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' or ' '
                ? c : '_')
            .ToArray());
        string disposition = $"{(download ? "attachment" : "inline")}; filename=\"{asciiFileName}\"; filename*=UTF-8''{Uri.EscapeDataString(safeFileName)}";

        var args = new PresignedGetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithExpiry((int)expiry.TotalSeconds)
            .WithHeaders(new Dictionary<string, string>
            {
                ["response-content-type"] = contentType,
                ["response-content-disposition"] = disposition
            });

        string url = await _presignClient.PresignedGetObjectAsync(args);
        cancellationToken.ThrowIfCancellationRequested();
        return url;
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
