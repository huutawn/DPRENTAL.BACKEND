namespace Dprental.MediaService.Domain.Entities;

/// <summary>
/// Đại diện cho một bản thể hiện nhỏ (variant) được tạo sẵn dưới định dạng WebP của MediaAsset.
/// </summary>
public class MediaImageVariant
{
    public Guid Id { get; set; }

    /// <summary>
    /// Định danh của MediaAsset sở hữu variant này.
    /// </summary>
    public Guid MediaAssetId { get; set; }

    /// <summary>
    /// Đối tượng MediaAsset liên kết.
    /// </summary>
    public MediaAsset? MediaAsset { get; set; }

    /// <summary>
    /// Tên biến thể (ví dụ: thumbnail, medium, large).
    /// </summary>
    public string VariantName { get; set; } = string.Empty;

    /// <summary>
    /// Phiên bản của thuật toán/pipeline xử lý hình ảnh (phục vụ backfill/nâng cấp).
    /// </summary>
    public int ProcessingVersion { get; set; } = 1;

    /// <summary>
    /// Khóa lưu trữ nội bộ (ObjectKey) của tệp WebP trên MinIO.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// MIME type của variant (chuẩn hóa: image/webp).
    /// </summary>
    public string ContentType { get; set; } = "image/webp";

    /// <summary>
    /// Chiều rộng theo pixel của variant.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Chiều cao theo pixel của variant.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Dung lượng tệp tính theo bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Mã băm SHA-256 của nội dung tệp WebP (chuỗi hex 64 ký tự thường).
    /// </summary>
    public string ChecksumSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Thời điểm tạo bản ghi.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
