namespace Dprental.ReviewService.Application.DTOs.Media;

/// <summary>
/// DTO xác minh thông tin media từ MediaService (GET /api/v1/media/{id}/verify).
/// </summary>
public sealed record MediaVerifyDto(
    Guid MediaId,
    Guid UploadedByUserId,
    string Status,
    string? ContentType);

/// <summary>
/// DTO phản hồi cấp quyền truy cập đọc riêng tư ngắn hạn từ MediaService (POST /api/v1/media/{id}/access).
/// </summary>
public sealed record MediaAccessDto(
    Guid MediaId,
    string Url,
    DateTimeOffset ExpiresAtUtc);
