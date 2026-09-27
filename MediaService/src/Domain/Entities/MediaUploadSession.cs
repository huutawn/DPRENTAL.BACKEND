using Dprental.MediaService.Common.Enums;

namespace Dprental.MediaService.Domain.Entities;

/// <summary>
/// Theo dõi upload nội bộ để xử lý retry, idempotency và dọn dẹp nếu request gián đoạn.
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
    /// Khóa Idempotency do client gửi hoặc khóa ngẫu nhiên khi client không gửi.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Mã băm của metadata và nội dung file để phát hiện trùng key nhưng khác dữ liệu.
    /// </summary>
    public string RequestPayloadHash { get; set; } = string.Empty;

    /// <summary>
    /// Khóa lưu trữ tạm thời trên MinIO, chỉ MediaService ghi.
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
