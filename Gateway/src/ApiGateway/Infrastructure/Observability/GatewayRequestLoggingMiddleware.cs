using System.Diagnostics;
using System.Globalization;
using Dprental.Gateway.Application.Observability;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ocelot.Configuration;
using Ocelot.Middleware;

namespace Dprental.Gateway.Infrastructure.Observability;

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
            double durationMs = Math.Round(elapsed.TotalMilliseconds, 2);

            WriteStructuredLog(context, durationMs);
        }
    }

    private void WriteStructuredLog(HttpContext context, double durationMs)
    {
        // 1. Trích xuất CorrelationId và TraceId
        string correlationId = context.TraceIdentifier;
        if (string.IsNullOrWhiteSpace(correlationId) &&
            context.Request.Headers.TryGetValue(GatewayHeaderConstants.CorrelationId, out var corrHeader))
        {
            correlationId = corrHeader.ToString();
        }

        string traceId = Activity.Current?.TraceId.ToString() ?? correlationId;

        // 2. Trích xuất Route và Upstream Service từ Ocelot nếu có (an toàn với các route không qua proxy như /health)
        DownstreamRoute? downstreamRoute = context.Items.DownstreamRoute();
        string route = !string.IsNullOrWhiteSpace(downstreamRoute?.Key)
            ? downstreamRoute.Key
            : downstreamRoute?.UpstreamPathTemplate?.OriginalValue ?? context.Request.Path.Value ?? "/";
        string upstream = !string.IsNullOrWhiteSpace(downstreamRoute?.ServiceName)
            ? downstreamRoute.ServiceName
            : (!string.IsNullOrWhiteSpace(downstreamRoute?.Key) ? downstreamRoute.Key : "gateway");
        string method = context.Request.Method;
        int statusCode = context.Response.StatusCode;
        string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // 3. Mở Logging Scope chứa đầy đủ các thuộc tính có cấu trúc (Structured Properties)
        // Không chứa Authorization, token, password, secret hay request body
        var logScope = new Dictionary<string, object?>
        {
            ["Timestamp"] = timestamp,
            ["CorrelationId"] = correlationId,
            ["TraceId"] = traceId,
            ["Route"] = route,
            ["Method"] = method,
            ["StatusCode"] = statusCode,
            ["DurationMs"] = durationMs,
            ["Upstream"] = upstream
        };

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
