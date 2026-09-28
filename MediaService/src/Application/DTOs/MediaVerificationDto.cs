namespace Dprental.MediaService.Application.DTOs;

/// <summary>
/// DTO trả về cho Backend nghiệp vụ để xác minh MediaId trước khi lưu tham chiếu.
/// </summary>
public sealed record MediaVerificationDto(
    Guid MediaId,
    string Status,
    string UploadedByUserId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);
