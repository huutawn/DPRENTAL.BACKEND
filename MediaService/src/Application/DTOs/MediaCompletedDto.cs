namespace Dprental.MediaService.Application.DTOs;

/// <summary>
/// Kết quả hoàn tất tải tệp và xác minh metadata.
/// Tuyệt đối không trả Bucket, ObjectKey hoặc vị trí lưu trữ nội bộ.
/// </summary>
public sealed record MediaCompletedDto(
    Guid MediaId,
    string Status,
    string OriginalFileName,
    string ContentType,
    long SizeBytes);
