namespace Dprental.Gateway.Application.Traffic;

/// <summary>
/// Cấu hình tổng thể về kiểm soát lưu lượng (Traffic Control), giới hạn tần suất (Rate Limiting)
/// và khả năng phục hồi (Resilience) của API Gateway.
/// </summary>
public sealed class GatewayTrafficOptions
{
    /// <summary>
    /// Tên section cấu hình trong file appsettings.json.
    /// </summary>
    public const string SectionName = "Gateway:Traffic";

    /// <summary>
    /// Cấu hình về thời gian chờ (Timeout) và cơ chế thử lại (Retry Safety).
    /// </summary>
    public GatewayResilienceOptions Resilience { get; set; } = new();

    /// <summary>
    /// Cấu hình về chính sách giới hạn tần suất request (Rate Limiting).
    /// </summary>
    public GatewayRateLimitingOptions RateLimiting { get; set; } = new();
}

/// <summary>
/// Cấu hình khả năng phục hồi (Resilience) và thời gian chờ (Timeout) cho Gateway.
/// </summary>
public sealed class GatewayResilienceOptions
{
    /// <summary>
    /// Thời gian chờ mặc định (tính bằng giây) trước khi Gateway ngắt kết nối và trả về 504 Gateway Timeout.
    /// Mặc định: 10 giây.
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Cho phép hay nghiêm cấm tự động thử lại (Retry) đối với các lệnh thay đổi trạng thái (POST, PATCH, v.v.).
    /// Mặc định là false nhằm đảm bảo an toàn nghiệp vụ, tránh tạo duplicate business effect.
    /// </summary>
    public bool AllowUnsafeCommandRetries { get; set; }
}

/// <summary>
/// Cấu hình chính sách Rate Limiting theo từng phân khúc người dùng và endpoint.
/// </summary>
public sealed class GatewayRateLimitingOptions
{
    /// <summary>
    /// Bật hoặc tắt tính năng Rate Limiting trên toàn Gateway. Mặc định là true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chính sách giới hạn tần suất cho người dùng ẩn danh (phân vùng theo Client IP).
    /// </summary>
    public RateLimitTierOptions Anonymous { get; set; } = new()
    {
        PermitLimit = 20,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// Chính sách giới hạn tần suất cho người dùng đã đăng nhập (phân vùng theo UserId).
    /// </summary>
    public RateLimitTierOptions Authenticated { get; set; } = new()
    {
        PermitLimit = 100,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// Chính sách giới hạn tần suất nghiêm ngặt cho các route nhạy cảm (đăng nhập, đổi mật khẩu, thanh toán).
    /// </summary>
    public RateLimitTierOptions Sensitive { get; set; } = new()
    {
        PermitLimit = 5,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// Danh sách các đường dẫn (Path Prefix) được áp dụng chính sách Sensitive Rate Limiting.
    /// </summary>
    public string[] SensitivePaths { get; set; } =
    [
        "/api/v1/auth/sessions",
        "/api/v1/auth/login",
        "/api/v1/auth/tokens"
    ];
}

/// <summary>
/// Thông số chi tiết cho một tầng (Tier) của chính sách Rate Limiting.
/// </summary>
public sealed class RateLimitTierOptions
{
    /// <summary>
    /// Số lượng request tối đa được phép thực thi trong một khung thời gian (Window).
    /// </summary>
    public int PermitLimit { get; set; } = 60;

    /// <summary>
    /// Kích thước khung thời gian tính bằng giây. Mặc định: 60 giây (1 phút).
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Số lượng request tối đa được xếp hàng đợi khi vượt ngưỡng trước khi bị từ chối với mã 429.
    /// Mặc định: 0 (từ chối ngay lập tức, không xếp hàng đợi).
    /// </summary>
    public int QueueLimit { get; set; }
}
