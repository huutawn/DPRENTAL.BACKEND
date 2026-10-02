namespace Dprental.CatalogService.Application.DTOs.Media;

/// <summary>
/// DTO chứa thông tin xác minh media asset từ MediaService.
/// </summary>
public sealed record MediaVerificationDto(
    Guid MediaId,
    string Status,
    string UploadedByUserId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);
