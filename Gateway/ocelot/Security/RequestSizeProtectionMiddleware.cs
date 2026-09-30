using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Security;

/// <summary>
/// Middleware bảo vệ API Gateway trước các request có dung lượng quá lớn (Request Size Protection),
/// ngăn ngừa nguy cơ cạn kiệt tài nguyên bộ nhớ và các cuộc tấn công từ chối dịch vụ (DoS/OOM).
/// </summary>
public sealed class RequestSizeProtectionMiddleware
{
    private static readonly Action<ILogger, PathString, long, long, Exception?> LogContentLengthExceeded = LoggerMessage.Define<PathString, long, long>(
        LogLevel.Warning,
        new EventId(1, "ContentLengthExceeded"),
        "Request tới đường dẫn {Path} bị từ chối do Content-Length ({ContentLength} bytes) vượt quá ngưỡng cho phép ({MaxBytes} bytes).");

    private static readonly Action<ILogger, PathString, long, Exception?> LogStreamPayloadExceeded = LoggerMessage.Define<PathString, long>(
        LogLevel.Warning,
        new EventId(2, "StreamPayloadExceeded"),
        "Request tới {Path} bị ngắt vì stream payload vượt quá kích thước tối đa cho phép {MaxBytes} bytes.");

    private readonly RequestDelegate next;
    private readonly IOptions<GatewaySecurityOptions> securityOptions;
    private readonly ILogger<RequestSizeProtectionMiddleware> logger;

    /// <summary>
    /// Khởi tạo middleware bảo vệ kích thước request.
    /// </summary>
    /// <param name="next">Delegate chuyển tiếp middleware tiếp theo.</param>
    /// <param name="securityOptions">Cấu hình bảo mật chứa thông số giới hạn dung lượng.</param>
    /// <param name="logger">Logger ghi log chẩn đoán.</param>
    public RequestSizeProtectionMiddleware(
        RequestDelegate next,
        IOptions<GatewaySecurityOptions> securityOptions,
        ILogger<RequestSizeProtectionMiddleware> logger)
    {
        this.next = next ?? throw new ArgumentNullException(nameof(next));
        this.securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Kiểm tra kích thước của Content-Length và thiết lập hạn mức tối đa cho request body stream.
    /// </summary>
    /// <param name="context">Ngữ cảnh HTTP của request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long maxSizeBytes = HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/v1/media/uploads", StringComparison.OrdinalIgnoreCase)
            ? securityOptions.Value.RequestSize.MediaUploadMaxRequestBodySizeBytes
            : securityOptions.Value.RequestSize.MaxRequestBodySizeBytes;

        // 1. Cấu hình giới hạn kích thước đọc của server feature (Kestrel) nếu có thể ghi
        IHttpMaxRequestBodySizeFeature? sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is not null && !sizeFeature.IsReadOnly)
        {
            sizeFeature.MaxRequestBodySize = maxSizeBytes;
        }

        // 2. Kiểm tra nhanh thông qua tiêu đề Content-Length
        if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > maxSizeBytes)
        {
            LogContentLengthExceeded(logger, context.Request.Path, context.Request.ContentLength.Value, maxSizeBytes, null);

            await GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                GatewayError.PayloadTooLarge,
                context.RequestAborted);
            return;
        }

        // 3. Xử lý các trường hợp đọc stream vượt quá ngưỡng quy định
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            LogStreamPayloadExceeded(logger, context.Request.Path, maxSizeBytes, null);

            await GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                GatewayError.PayloadTooLarge,
                context.RequestAborted);
        }
    }
}
