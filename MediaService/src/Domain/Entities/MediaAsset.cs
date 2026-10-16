using Dprental.MediaService.Common.Enums;

namespace Dprental.MediaService.Domain.Entities;

/// <summary>
/// Đại diện cho một tài nguyên media trong hệ thống.
/// </summary>
public class MediaAsset
{
    public Guid Id { get; set; }

    /// <summary>
    /// Trạng thái vòng đời của media asset.
    /// </summary>
    public MediaStatus Status { get; set; } = MediaStatus.PendingUpload;

    /// <summary>
    /// Trạng thái hiển thị (Private hoặc Public). Mặc định là Private.
    /// </summary>
    public MediaVisibility Visibility { get; set; } = MediaVisibility.Private;

    /// <summary>
    /// Tên tệp gốc đã được chuẩn hóa và loại bỏ các ký tự độc hại / path traversal.
    /// </summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME type do client khai báo khi khởi tạo phiên upload.
    /// </summary>
    public string DeclaredContentType { get; set; } = string.Empty;

    /// <summary>
    /// Dung lượng do client khai báo khi khởi tạo phiên upload (bytes).
    /// </summary>
    public long DeclaredSizeBytes { get; set; }

    /// <summary>
    /// Mã băm SHA-256 do client khai báo nếu có (chuỗi hex 64 ký tự).
    /// </summary>
    public string? DeclaredChecksumSha256 { get; set; }

    /// <summary>
    /// MIME type thực tế được phát hiện qua magic bytes / file inspection.
    /// </summary>
    public string? ActualContentType { get; set; }

    /// <summary>
    /// Dung lượng thực tế của tệp sau khi tải lên và kiểm tra (bytes).
    /// </summary>
    public long? ActualSizeBytes { get; set; }

    /// <summary>
    /// Mã băm SHA-256 thực tế được tính toán từ nội dung tệp (chuỗi hex).
    /// </summary>
    public string? ActualChecksumSha256 { get; set; }

    /// <summary>
    /// Khóa lưu trữ nội bộ (ObjectKey) chính thức trên Object Storage (không bao giờ lộ ra ngoài API).
    /// </summary>
    public string? StorageKey { get; set; }

    /// <summary>
    /// Định danh người dùng thực hiện tải tệp lên.
    /// </summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// Lý do từ chối nếu tệp không hợp lệ.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// Thời điểm tạo bản ghi.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Thời điểm cập nhật cuối cùng.
    /// </summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Phiên bản phục vụ Optimistic Concurrency Control.
    /// </summary>
    public long Version { get; set; } = 1;

    /// <summary>
    /// Danh sách các phiên tải lên liên quan đến asset này.
    /// </summary>
    public ICollection<MediaUploadSession> UploadSessions { get; set; } = new List<MediaUploadSession>();

    /// <summary>
    /// Danh sách các biến thể hình ảnh (variants) đã được tạo cho asset này.
    /// </summary>
    public ICollection<MediaImageVariant> ImageVariants { get; set; } = new List<MediaImageVariant>();
}
