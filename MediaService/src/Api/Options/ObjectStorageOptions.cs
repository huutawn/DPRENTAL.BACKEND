namespace Dprental.MediaService.Api.Options;

/// <summary>
/// Cấu hình kết nối lưu trữ đối tượng MinIO/S3.
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>
    /// Tên section cấu hình trong appsettings hoặc User Secrets.
    /// </summary>
    public const string SectionName = "ObjectStorage";

    /// <summary>
    /// Endpoint dịch vụ MinIO (ví dụ: http://127.0.0.1:9000 hoặc 127.0.0.1:9000).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Tên bucket dùng để lưu trữ file media.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Khóa truy cập (Access Key / Username).
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Khóa bí mật (Secret Key / Password).
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Tùy chọn bật/tắt kết nối SSL/TLS.
    /// </summary>
    public bool? UseSsl { get; set; }

    /// <summary>
    /// Endpoint trình duyệt truy cập được cho URL GET ký sẵn.
    /// Nếu để trống, hệ thống sẽ sử dụng Endpoint nội bộ.
    /// </summary>
    public string? BrowserEndpoint { get; set; }

    /// <summary>
    /// Danh sách các Origin được phép gọi CORS khi upload từ trình duyệt.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Thời hạn URL GET ký sẵn, tính bằng giây (mặc định 5 phút).</summary>
    public int ReadUrlExpirySeconds { get; set; } = 300;
}
