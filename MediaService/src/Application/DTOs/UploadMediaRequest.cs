namespace Dprental.MediaService.Application.DTOs;

/// <summary>Input của một lần upload file; Stream do caller sở hữu và đóng sau khi use case kết thúc.</summary>
public sealed record UploadMediaRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    Stream Content,
    string? Sha256Checksum = null,
    int BatchSize = 1)
{
    public const int MaximumBatchSize = 10;
}
