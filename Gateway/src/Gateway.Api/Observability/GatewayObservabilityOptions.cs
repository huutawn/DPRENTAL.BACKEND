namespace Dprental.Gateway.Observability;

/// <summary>
/// Cấu hình tổng thể về giám sát vận hành (Observability), bao gồm Structured Logging và Metrics.
/// </summary>
public sealed class GatewayObservabilityOptions
{
    /// <summary>
    /// Tên section cấu hình trong file appsettings.json.
    /// </summary>
    public const string SectionName = "Gateway:Observability";

    /// <summary>
    /// Cấu hình ghi log có cấu trúc (Structured Logging) cho mọi request đi qua Gateway.
    /// </summary>
    public GatewayLoggingOptions Logging { get; set; } = new();

    /// <summary>
    /// Cấu hình thu thập và xuất chỉ số đo lường (Metrics).
    /// </summary>
    public GatewayMetricsOptions Metrics { get; set; } = new();
}

/// <summary>
/// Cấu hình chi tiết cho bộ ghi log có cấu trúc của Gateway.
/// </summary>
public sealed class GatewayLoggingOptions
{
    /// <summary>
    /// Bật hoặc tắt tính năng ghi log chi tiết cho từng request. Mặc định là true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cho phép ghi nhận query string trong log hay không (nếu true, cần chú ý không để lộ secret).
    /// Mặc định: false.
    /// </summary>
    public bool IncludeQueryString { get; set; }

    /// <summary>
    /// Danh sách các tiêu đề HTTP nhạy cảm cần ẩn (redact) hoặc loại bỏ hoàn toàn khỏi nội dung log
    /// nhằm chống rò rỉ token, mật khẩu hay thông tin định danh bí mật.
    /// </summary>
    public string[] RedactedHeaderNames { get; set; } =
    [
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "token",
        "password",
        "secret"
    ];
}

/// <summary>
/// Cấu hình chỉ số đo lường vận hành của Gateway.
/// </summary>
public sealed class GatewayMetricsOptions
{
    /// <summary>
    /// Bật hoặc tắt tính năng thu thập metrics. Mặc định là true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
