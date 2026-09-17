using System.Diagnostics.Metrics;
using System.Globalization;
using Dprental.Gateway.Domain.Observability;

namespace Dprental.Gateway.Infrastructure.Observability;

/// <summary>
/// Triển khai thu thập chỉ số đo lường (Metrics) của API Gateway sử dụng System.Diagnostics.Metrics.Meter chuẩn hóa.
/// Tương thích trực tiếp với OpenTelemetry, Prometheus và các hệ thống giám sát hiện đại.
/// Tuyệt đối chỉ sử dụng các nhãn (Labels) có cardinality thấp (route, method, status, upstream), không chứa PII.
/// </summary>
public sealed class GatewayMetrics : IGatewayMetrics, IDisposable
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
            description: "Tổng số request bị upstream timeout (504).");

        rateLimitCounter = meter.CreateCounter<long>(
            GatewayMetricNames.RateLimitTotal,
            unit: "{request}",
            description: "Tổng số request bị rate limit (429).");
    }

    /// <inheritdoc />
    public void RecordRequest(string route, string method, int statusCode, double durationSeconds, string upstreamService)
    {
        string statusStr = statusCode.ToString(CultureInfo.InvariantCulture);

        KeyValuePair<string, object?>[] tags =
        [
            new(GatewayMetricNames.LabelRoute, string.IsNullOrWhiteSpace(route) ? "unknown" : route),
            new(GatewayMetricNames.LabelMethod, string.IsNullOrWhiteSpace(method) ? "unknown" : method),
            new(GatewayMetricNames.LabelStatus, statusStr),
            new(GatewayMetricNames.LabelUpstream, string.IsNullOrWhiteSpace(upstreamService) ? "none" : upstreamService)
        ];

        requestCounter.Add(1, tags);
        durationHistogram.Record(durationSeconds, tags);

        if (statusCode >= 400 && statusCode < 500)
        {
            error4xxCounter.Add(1, tags);
        }
        else if (statusCode >= 500 && statusCode < 600)
        {
            error5xxCounter.Add(1, tags);
        }
    }

    /// <inheritdoc />
    public void RecordUpstreamTimeout(string route, string method, string upstreamService)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new(GatewayMetricNames.LabelRoute, string.IsNullOrWhiteSpace(route) ? "unknown" : route),
            new(GatewayMetricNames.LabelMethod, string.IsNullOrWhiteSpace(method) ? "unknown" : method),
            new(GatewayMetricNames.LabelUpstream, string.IsNullOrWhiteSpace(upstreamService) ? "none" : upstreamService)
        ];

        upstreamTimeoutCounter.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordRateLimitExceeded(string route, string method)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new(GatewayMetricNames.LabelRoute, string.IsNullOrWhiteSpace(route) ? "unknown" : route),
            new(GatewayMetricNames.LabelMethod, string.IsNullOrWhiteSpace(method) ? "unknown" : method)
        ];

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
