namespace Dprental.ReviewService.Application.Options;

/// <summary>
/// Cấu hình kết nối tới RentalService nội bộ.
/// </summary>
public sealed class RentalServiceOptions
{
    /// <summary>
    /// Tên section trong appsettings.json.
    /// </summary>
    public const string SectionName = "RentalService";

    /// <summary>
    /// Địa chỉ cơ sở của RentalService (ví dụ: http://rental-service:5000).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Khóa API nội bộ truyền qua header X-Api-Key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian chờ tối đa cho mỗi cuộc gọi (mặc định 5 giây).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;
}
