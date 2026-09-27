namespace Dprental.MediaService.Domain.IStorage;

/// <summary>
/// Metadata cơ bản của object trong Object Storage.
/// </summary>
public sealed record ObjectMetadataResult(
    long ContentLength,
    string ContentType,
    string? ETag);
