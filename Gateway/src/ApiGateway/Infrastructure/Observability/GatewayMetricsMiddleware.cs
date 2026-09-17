using System.Diagnostics;
using Dprental.Gateway.Application.Observability;
using Dprental.Gateway.Domain.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Dprental.Gateway.Infrastructure.Observability;

/// <summary>
/// Middleware thu thập các chỉ số đo lường hiệu năng và lưu lượng (Metrics Instrumentation)
/// cho mọi request qua Gateway.
/// </summary>
public sealed class GatewayMetricsMiddleware
{
    private readonly RequestDelegate next;
    private readonly IGatewayMetrics metrics;
    private readonly IOptions<GatewayObservabilityOptions> options;

    /// <summary>
    /// Khởi tạo middleware thu thập metrics.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="metrics">Dịch vụ ghi nhận metrics.</param>
    /// <param name="options">Cấu hình giám sát Gateway.</param>
    public GatewayMetricsMiddleware(
        RequestDelegate next,
        IGatewayMetrics metrics,
        IOptions<GatewayObservabilityOptions> options)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Đo lường thời gian thực thi request và cập nhật các chỉ số đo lường.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!options.Value.Metrics.Enabled)
        {
            await next(context);
            return;
        }

        long startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            double durationSeconds = elapsed.TotalSeconds;

            RecordMetrics(context, durationSeconds);
        }
    }

    private void RecordMetrics(HttpContext context, double durationSeconds)
    {
        IReverseProxyFeature? proxyFeature = context.Features.Get<IReverseProxyFeature>();
        string route = proxyFeature?.Route?.Config?.RouteId ?? context.Request.Path.Value ?? "/";
        string upstream = proxyFeature?.Cluster?.Config?.ClusterId ?? "gateway";
        string method = context.Request.Method;
        int statusCode = context.Response.StatusCode;

        // 1. Ghi nhận request và thời gian xử lý (kèm phân loại lỗi 4xx/5xx nếu có)
        metrics.RecordRequest(route, method, statusCode, durationSeconds, upstream);

        // 2. Ghi nhận metric chuyên biệt cho Upstream Timeout (504)
        if (statusCode == StatusCodes.Status504GatewayTimeout)
        {
            metrics.RecordUpstreamTimeout(route, method, upstream);
        }

        // 3. Ghi nhận metric chuyên biệt cho Rate Limit Exceeded (429)
        if (statusCode == StatusCodes.Status429TooManyRequests)
        {
            metrics.RecordRateLimitExceeded(route, method);
        }
    }
}
