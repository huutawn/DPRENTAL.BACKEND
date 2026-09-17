using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Infrastructure.Traffic.Resilience;

/// <summary>
/// Middleware thực thi ngân sách thời gian chờ (Timeout Budget) cho các request chuyển tiếp tới upstream services.
/// Đảm bảo Gateway chủ động ngắt kết nối và trả về 504 Gateway Timeout khi upstream không phản hồi kịp,
/// đồng thời chuyển tiếp tín hiệu hủy (Cancellation) xuống downstream nếu client ngắt kết nối.
/// </summary>
public sealed class UpstreamTimeoutMiddleware
{
    private static readonly Action<ILogger, PathString, int, Exception?> LogUpstreamTimeout = LoggerMessage.Define<PathString, int>(
        LogLevel.Warning,
        new EventId(1, "UpstreamTimeoutExceeded"),
        "Request tới {Path} đã vượt quá ngân sách thời gian chờ ({TimeoutSeconds} giây). Đang trả về 504 Gateway Timeout.");

    private readonly RequestDelegate next;
    private readonly IOptions<GatewayTrafficOptions> options;
    private readonly ILogger<UpstreamTimeoutMiddleware> logger;

    /// <summary>
    /// Khởi tạo middleware quản lý thời gian chờ upstream.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="options">Cấu hình điều phối lưu lượng và thời gian chờ.</param>
    /// <param name="logger">Logger ghi log chẩn đoán.</param>
    public UpstreamTimeoutMiddleware(
        RequestDelegate next,
        IOptions<GatewayTrafficOptions> options,
        ILogger<UpstreamTimeoutMiddleware> logger)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Thực thi kiểm soát thời gian chờ dựa trên CancellationToken liên kết.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int timeoutSeconds = options.Value.Resilience.DefaultTimeoutSeconds;
        CancellationToken originalClientToken = context.RequestAborted;

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            originalClientToken,
            timeoutCts.Token);

        // Gán token liên kết vào context.RequestAborted để YARP forwarder và downstream nhận biết
        context.RequestAborted = linkedCts.Token;

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !originalClientToken.IsCancellationRequested)
        {
            context.Items["Gateway.TimeoutExceeded"] = true;
            // Upstream không phản hồi kịp trong phạm vi timeout budget và client chưa ngắt kết nối
            if (!context.Response.HasStarted)
            {
                LogUpstreamTimeout(logger, context.Request.Path, timeoutSeconds, null);

                await GatewayErrorResponseWriter.WriteAsync(
                    context,
                    StatusCodes.Status504GatewayTimeout,
                    GatewayError.UpstreamTimeout,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (originalClientToken.IsCancellationRequested)
        {
            // Client chủ động ngắt kết nối (Client Cancellation) -> cho phép hủy bình thường
            throw;
        }
        finally
        {
            if (timeoutCts.IsCancellationRequested && !originalClientToken.IsCancellationRequested)
            {
                context.Items["Gateway.TimeoutExceeded"] = true;
            }

            // Khôi phục lại token ban đầu của context
            context.RequestAborted = originalClientToken;
        }
    }
}
