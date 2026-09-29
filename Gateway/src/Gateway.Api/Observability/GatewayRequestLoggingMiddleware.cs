using System.Diagnostics;
using System.Globalization;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ocelot.Configuration;
using Ocelot.Middleware;

namespace Dprental.Gateway.Observability;

/// <summary>
/// Middleware ghi log có cấu trúc (Structured Logging) cho mọi request đi qua API Gateway.
/// Đảm bảo ghi nhận đầy đủ: timestamp, correlationId, traceId, route, HTTP method, status code, duration, upstream service.
/// Đồng thời TUYỆT ĐỐI KHÔNG ghi nhận Authorization header, JWT, token, mật khẩu hay sensitive request body.
/// </summary>
public sealed partial class GatewayRequestLoggingMiddleware
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "[{Timestamp}] {Method} {Route} -> {StatusCode} in {DurationMs}ms (TraceId={TraceId}, CorrelationId={CorrelationId}, Upstream={Upstream})")]
    private static partial void LogRequestCompleted(
        ILogger logger,
        string timestamp,
        string method,
        string route,
        string traceId,
        string correlationId,
        int statusCode,
        double durationMs,
        string upstream);

    private readonly RequestDelegate next;
    private readonly IOptions<GatewayObservabilityOptions> options;
    private readonly ILogger<GatewayRequestLoggingMiddleware> logger;

    /// <summary>
    /// Khởi tạo middleware ghi log có cấu trúc.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="options">Cấu hình ghi log của Gateway.</param>
    /// <param name="logger">Logger chuyên biệt cho Gateway Request Logging.</param>
    public GatewayRequestLoggingMiddleware(
        RequestDelegate next,
        IOptions<GatewayObservabilityOptions> options,
        ILogger<GatewayRequestLoggingMiddleware> logger)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Đo lường thời gian thực thi request và xuất log có cấu trúc chi tiết khi hoàn tất.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!options.Value.Logging.Enabled)
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
            double durationMs = elapsed.TotalMilliseconds;

            WriteStructuredLog(context, durationMs);
        }
    }

    private void WriteStructuredLog(HttpContext context, double durationMs)
    {
        DownstreamRoute? downstreamRoute = context.Items.DownstreamRoute();
        string route = !string.IsNullOrWhiteSpace(downstreamRoute?.Key)
            ? downstreamRoute.Key
            : downstreamRoute?.UpstreamPathTemplate?.OriginalValue ?? context.Request.Path.Value ?? "/";
        string upstream = !string.IsNullOrWhiteSpace(downstreamRoute?.ServiceName)
            ? downstreamRoute.ServiceName
            : (!string.IsNullOrWhiteSpace(downstreamRoute?.Key) ? downstreamRoute.Key : "gateway");
        string method = context.Request.Method;
        int statusCode = context.Response.StatusCode;

        string correlationId = context.TraceIdentifier;
        if (string.IsNullOrWhiteSpace(correlationId) &&
            context.Request.Headers.TryGetValue(GatewayHeaderConstants.CorrelationId, out var corrHeader))
        {
            correlationId = corrHeader.ToString();
        }

        string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        string timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        Dictionary<string, object> logScope = new()
        {
            ["Timestamp"] = timestamp,
            ["Method"] = method,
            ["Route"] = route,
            ["StatusCode"] = statusCode,
            ["DurationMs"] = durationMs,
            ["TraceId"] = traceId,
            ["CorrelationId"] = correlationId,
            ["Upstream"] = upstream,
            ["ClientIp"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
        };

        if (options.Value.Logging.IncludeQueryString && context.Request.QueryString.HasValue)
        {
            logScope["QueryString"] = context.Request.QueryString.Value;
        }

        using (logger.BeginScope(logScope))
        {
            LogRequestCompleted(
                logger,
                timestamp,
                method,
                route,
                traceId,
                correlationId,
                statusCode,
                durationMs,
                upstream);
        }
    }
}
