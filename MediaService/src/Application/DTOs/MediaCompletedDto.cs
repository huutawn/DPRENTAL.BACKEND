namespace Dprental.MediaService.Application.DTOs;

/// <summary>
/// Kết quả hoàn tất tải tệp và xác minh metadata.
/// Không trả riêng khóa lưu trữ nội bộ; URL preview ký ngắn hạn có thể chứa
/// bucket và object key theo giao thức S3.
/// </summary>
public sealed record MediaCompletedDto(
    Guid MediaId,
    string Status,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string? PreviewUrl = null,
    DateTimeOffset? PreviewUrlExpiresAtUtc = null,
    IReadOnlyList<MediaImageVariantDto>? ImageVariants = null)
{
    public IReadOnlyList<MediaImageVariantDto> ImageVariants { get; init; } = ImageVariants ?? [];
}
