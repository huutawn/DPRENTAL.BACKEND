using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Dprental.Gateway.Observability;

/// <summary>
/// Triển khai thu thập chỉ số đo lường (Metrics) của API Gateway sử dụng System.Diagnostics.Metrics.Meter chuẩn hóa.
/// Tương thích trực tiếp với OpenTelemetry, Prometheus và các hệ thống giám sát hiện đại.
/// Tuyệt đối chỉ sử dụng các nhãn (Labels) có cardinality thấp (route, method, status, upstream), không chứa PII.
/// </summary>
public sealed class GatewayMetrics : IDisposable
{
    private readonly Meter meter;
    private readonly Counter<long> requestCounter;
    private readonly Histogram<double> durationHistogram;
    private readonly Counter<long> error4xxCounter;
    private readonly Counter<long> error5xxCounter;
    private readonly Counter<long> upstreamTimeoutCounter;
    private readonly Counter<long> rateLimitCounter;

    /// <summary>
    /// Khởi tạo Meter và các Metric Instruments đo lường.
    /// </summary>
    public GatewayMetrics()
    {
        meter = new Meter(GatewayMetricNames.MeterName, "1.0.0");

        requestCounter = meter.CreateCounter<long>(
            GatewayMetricNames.RequestTotal,
            unit: "{request}",
            description: "Tổng số lượng HTTP requests đi qua API Gateway.");

        durationHistogram = meter.CreateHistogram<double>(
            GatewayMetricNames.RequestDuration,
            unit: "s",
            description: "Thời gian xử lý request qua Gateway tính bằng giây.");

        error4xxCounter = meter.CreateCounter<long>(
            GatewayMetricNames.Error4xxTotal,
            unit: "{response}",
            description: "Tổng số response lỗi client 4xx.");

        error5xxCounter = meter.CreateCounter<long>(
            GatewayMetricNames.Error5xxTotal,
            unit: "{response}",
            description: "Tổng số response lỗi server 5xx.");

        upstreamTimeoutCounter = meter.CreateCounter<long>(
            GatewayMetricNames.UpstreamTimeoutTotal,
            unit: "{request}",
            description: "Tổng số request bị upstream timeout (Ocelot 503 hoặc Gateway 504).");

        rateLimitCounter = meter.CreateCounter<long>(
            GatewayMetricNames.RateLimitTotal,
            unit: "{request}",
            description: "Tổng số request bị rate limit (429).");
    }

    /// <summary>
    /// Ghi nhận một request hoàn thành: tăng biến đếm tổng request, ghi nhận thời gian xử lý (duration)
    /// và phân loại lỗi 4xx/5xx nếu có.
    /// </summary>
    public void RecordRequest(string route, string method, int statusCode, double durationSeconds, string upstreamService)
    {
        string statusStr = statusCode.ToString(CultureInfo.InvariantCulture);

        TagList tags = new()
        {
            { GatewayMetricNames.LabelRoute, route },
            { GatewayMetricNames.LabelMethod, method },
            { GatewayMetricNames.LabelStatus, statusStr },
            { GatewayMetricNames.LabelUpstream, upstreamService }
        };

        requestCounter.Add(1, tags);
        durationHistogram.Record(durationSeconds, tags);

        if (statusCode >= 400 && statusCode <= 499)
        {
            error4xxCounter.Add(1, tags);
        }
        else if (statusCode >= 500 && statusCode <= 599)
        {
            error5xxCounter.Add(1, tags);
        }
    }

    /// <summary>
    /// Ghi nhận một trường hợp request bị timeout khi chờ dịch vụ upstream.
    /// </summary>
    public void RecordUpstreamTimeout(string route, string method, string upstreamService)
    {
        TagList tags = new()
        {
            { GatewayMetricNames.LabelRoute, route },
            { GatewayMetricNames.LabelMethod, method },
            { GatewayMetricNames.LabelUpstream, upstreamService }
        };

        upstreamTimeoutCounter.Add(1, tags);
    }

    /// <summary>
    /// Ghi nhận một trường hợp request bị từ chối do vượt hạn mức tần suất (HTTP 429).
    /// </summary>
    public void RecordRateLimitExceeded(string route, string method)
    {
        TagList tags = new()
        {
            { GatewayMetricNames.LabelRoute, route },
            { GatewayMetricNames.LabelMethod, method }
        };

        rateLimitCounter.Add(1, tags);
    }

    /// <summary>
    /// Giải phóng tài nguyên Meter khi ứng dụng dừng.
    /// </summary>
    public void Dispose()
    {
        meter.Dispose();
    }
}
