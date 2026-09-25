namespace Dprental.MediaService.Application.DTOs;

/// <summary>
/// Yêu cầu khởi tạo phiên tải tệp từ Client.
/// </summary>
public sealed record InitializeUploadRequest(
    string Purpose,
    string OwnerEntityId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? Sha256Checksum = null);

/// <summary>
/// Kết quả khởi tạo phiên tải tệp trả về cho Client.
/// </summary>
public sealed record UploadSessionDto(
    Guid MediaId,
    Guid UploadId,
    string UploadUrl,
    string Method,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAtUtc);
