namespace Dprental.MediaService.Application.DTOs;

/// <summary>
/// DTO đại diện cho một biến thể hình ảnh (variant) sẵn sàng phục vụ client/CDN.
/// </summary>
public sealed record MediaImageVariantDto(
    string Name,
    int Width,
    int Height,
    string ContentType,
    long SizeBytes,
    string ContentPath);
