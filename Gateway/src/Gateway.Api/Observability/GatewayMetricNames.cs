namespace Dprental.Gateway.Observability;

/// <summary>
/// Định nghĩa các tên chỉ số đo lường (Metrics) và nhãn (Labels/Dimensions) chuẩn hóa của API Gateway.
/// Tuân thủ quy ước OpenTelemetry và Prometheus, chỉ dùng nhãn có cardinality thấp (route, method, status, upstream),
/// tuyệt đối không sử dụng userId, email, orderId hay thông tin định danh cá nhân (PII).
/// </summary>
public static class GatewayMetricNames
{
    /// <summary>
    /// Tên Meter chuẩn của API Gateway phục vụ đăng ký OpenTelemetry và System.Diagnostics.Metrics.
    /// </summary>
    public const string MeterName = "Dprental.Gateway";

    // --- Tên các Metric Instruments ---

    /// <summary>
    /// Tổng số lượng HTTP request đã đi qua API Gateway.
    /// </summary>
    public const string RequestTotal = "gateway_request_total";

    /// <summary>
    /// Thời gian xử lý và chuyển tiếp request của Gateway tính bằng giây (Histogram).
    /// </summary>
    public const string RequestDuration = "gateway_request_duration_seconds";

    /// <summary>
    /// Tổng số lượng response trả về mã lỗi client 4xx (400-499).
    /// </summary>
    public const string Error4xxTotal = "gateway_4xx_total";

    /// <summary>
    /// Tổng số lượng response trả về mã lỗi server 5xx (500-599).
    /// </summary>
    public const string Error5xxTotal = "gateway_5xx_total";

    /// <summary>
    /// Tổng số lượng request bị timeout khi chờ downstream service phản hồi.
    /// </summary>
    public const string UpstreamTimeoutTotal = "gateway_upstream_timeout_total";

    /// <summary>
    /// Tổng số lượng request bị từ chối do vượt quá hạn mức tần suất (Rate Limit - 429).
    /// </summary>
    public const string RateLimitTotal = "gateway_rate_limit_total";

    // --- Tên các nhãn (Labels/Dimensions) an toàn cho phép ---

    /// <summary>
    /// Nhãn tên route hoặc pattern đường dẫn (ví dụ: "auth-service", "/api/v1/auth/{**catch-all}").
    /// </summary>
    public const string LabelRoute = "route";

    /// <summary>
    /// Nhãn phương thức HTTP (ví dụ: "GET", "POST", "PUT", "DELETE").
    /// </summary>
    public const string LabelMethod = "method";

    /// <summary>
    /// Nhãn mã trạng thái HTTP (ví dụ: "200", "401", "403", "429", "502", "503").
    /// </summary>
    public const string LabelStatus = "status";

    /// <summary>
    /// Nhãn tên cụm downstream service mục tiêu (ví dụ: "auth-service", "rental-service", "gateway").
    /// </summary>
    public const string LabelUpstream = "upstream";
}
