namespace Dprental.Gateway.Domain.Observability;

/// <summary>
/// Giao diện ghi nhận các chỉ số đo lường vận hành (Operational Metrics) của API Gateway.
/// </summary>
public interface IGatewayMetrics
{
    /// <summary>
    /// Ghi nhận một request hoàn thành: tăng biến đếm tổng request, ghi nhận thời gian xử lý (duration)
    /// và phân loại lỗi 4xx/5xx nếu có.
    /// </summary>
    /// <param name="route">Tên route hoặc pattern tuyến đường.</param>
    /// <param name="method">Phương thức HTTP của request.</param>
    /// <param name="statusCode">Mã trạng thái phản hồi HTTP.</param>
    /// <param name="durationSeconds">Thời gian xử lý tính bằng giây.</param>
    /// <param name="upstreamService">Tên dịch vụ đích upstream hoặc "gateway".</param>
    void RecordRequest(string route, string method, int statusCode, double durationSeconds, string upstreamService);

    /// <summary>
    /// Ghi nhận một trường hợp request bị timeout khi chờ dịch vụ upstream.
    /// </summary>
    /// <param name="route">Tên route hoặc pattern tuyến đường.</param>
    /// <param name="method">Phương thức HTTP.</param>
    /// <param name="upstreamService">Tên dịch vụ đích bị timeout.</param>
    void RecordUpstreamTimeout(string route, string method, string upstreamService);

    /// <summary>
    /// Ghi nhận một trường hợp request bị từ chối do vượt hạn mức tần suất (HTTP 429).
    /// </summary>
    /// <param name="route">Tên route hoặc pattern tuyến đường.</param>
    /// <param name="method">Phương thức HTTP.</param>
    void RecordRateLimitExceeded(string route, string method);
}
