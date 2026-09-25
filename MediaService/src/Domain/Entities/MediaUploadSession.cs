using Dprental.MediaService.Common.Enums;

namespace Dprental.MediaService.Domain.Entities;

/// <summary>
/// Đại diện cho một phiên tải lên tệp trực tiếp từ client lên MinIO.
/// </summary>
public class MediaUploadSession
{
    public Guid Id { get; set; }

    /// <summary>
    /// Định danh của MediaAsset liên kết.
    /// </summary>
    public Guid MediaAssetId { get; set; }

    /// <summary>
    /// MediaAsset liên kết với phiên upload này.
    /// </summary>
    public MediaAsset? MediaAsset { get; set; }

    /// <summary>
    /// Danh tính của caller khởi tạo phiên (User ID).
    /// </summary>
    public string CallerIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Khóa Idempotency được client gửi kèm để chống trùng lặp phiên.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Mã băm của toàn bộ nội dung yêu cầu khởi tạo để phát hiện xung đột dữ liệu cùng IdempotencyKey.
    /// </summary>
    public string RequestPayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// Khóa lưu trữ tạm thời trên MinIO được cấp URL ký sẵn PUT (ngẫu nhiên, không chứa tên file gốc).
    /// </summary>
    public string TemporaryStorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái của phiên upload.
    /// </summary>
    public UploadSessionStatus Status { get; set; } = UploadSessionStatus.Pending;

    /// <summary>
    /// Thời điểm hết hạn của phiên upload (15 phút sau khi tạo).
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>
    /// Thời điểm khởi tạo phiên.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Thời điểm phiên được hoàn tất hoặc từ chối.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>
    /// Phiên bản phục vụ Optimistic Concurrency Control.
    /// </summary>
    public long Version { get; set; } = 1;
}
