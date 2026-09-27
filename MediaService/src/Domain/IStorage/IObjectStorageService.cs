namespace Dprental.MediaService.Domain.IStorage;

/// <summary>
/// Hợp đồng trừu tượng cho các thao tác với Object Storage (MinIO/S3).
/// </summary>
public interface IObjectStorageService
{
    /// <summary>
    /// Sinh URL ký sẵn để client tải tệp trực tiếp lên MinIO bằng phương thức PUT.
    /// </summary>
    Task<PresignedUploadUrlResult> GeneratePresignedUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy metadata của object trên storage.
    /// </summary>
    Task<ObjectMetadataResult?> GetObjectMetadataAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc stream nội dung của object để phục vụ xác minh magic bytes và tính checksum SHA-256.
    /// </summary>
    Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sao chép/chuyển object từ khóa tạm sang khóa lưu trữ chính thức.
    /// </summary>
    Task CopyObjectAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Xóa object khỏi storage.
    /// </summary>
    Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default);
}
