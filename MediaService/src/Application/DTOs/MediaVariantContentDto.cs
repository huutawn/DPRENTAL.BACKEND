namespace Dprental.MediaService.Application.DTOs;

public sealed record MediaVariantContentDto(
    Guid MediaId,
    string VariantName,
    string ContentType,
    long SizeBytes,
    string ChecksumSha256,
    Stream? ContentStream = null,
    bool IsNotModified = false,
    bool FallbackToOriginal = false);
