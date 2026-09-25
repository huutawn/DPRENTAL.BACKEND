namespace Dprental.MediaService.Domain.IStorage;

/// <summary>
/// Kết quả sinh URL ký sẵn cho thao tác tải tệp trực tiếp lên MinIO.
/// </summary>
public sealed record PresignedUploadUrlResult(
    string UploadUrl,
    string Method,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Metadata cơ bản của object trong Object Storage.
/// </summary>
public sealed record ObjectMetadataResult(
    long ContentLength,
    string ContentType,
    string? ETag);
