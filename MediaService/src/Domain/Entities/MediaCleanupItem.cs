using Dprental.MediaService.Common.Enums;

namespace Dprental.MediaService.Domain.Entities;

/// <summary>
/// Bản ghi đánh dấu object trên Object Storage cần được dọn dẹp có hỗ trợ retry.
/// </summary>
public class MediaCleanupItem
{
    public Guid Id { get; set; }

    /// <summary>
    /// Khóa lưu trữ của object cần xóa trên MinIO.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Lý do cần dọn dẹp (ví dụ: TemporaryObjectCompleted, RejectedUpload, ExpiredSession).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Trạng thái của tác vụ dọn dẹp.
    /// </summary>
    public CleanupItemStatus Status { get; set; } = CleanupItemStatus.Pending;

    /// <summary>
    /// Số lần đã thử thực hiện dọn dẹp.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Lỗi gần nhất nếu quá trình xóa thất bại.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Thời điểm tạo tác vụ dọn dẹp.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Không xóa khóa tạm trước khi URL PUT đã hết hạn; URL còn hạn có thể ghi lại object.
    /// </summary>
    public DateTimeOffset EligibleAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Thời điểm tác vụ được xử lý hoàn tất hoặc dừng retry.
    /// </summary>
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
