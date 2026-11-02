using Dprental.Gateway.Models.Errors;
using Dprental.Gateway.Extensions.Errors;
namespace Dprental.Gateway.Middlewares.Errors;

/// <summary>Chuyển ngoại lệ chưa xử lý thành lỗi chuẩn của Gateway và lưu chi tiết chẩn đoán trong log.</summary>
internal sealed class GatewayExceptionMiddleware(RequestDelegate next, ILogger<GatewayExceptionMiddleware> logger)
{
    private static readonly Action<ILogger, PathString, Exception?> LogUnexpectedException = LoggerMessage.Define<PathString>(
        LogLevel.Error,
        new EventId(1, "UnhandledGatewayException"),
        "Unhandled Gateway exception for {RequestPath}.");

    /// <summary>Chạy middleware tiếp theo và chuyển các lỗi máy chủ chưa được xử lý thành phản hồi HTTP.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        // Client ngắt kết nối là hủy request, không chuyển thành lỗi máy chủ 500.
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Chỉ thay phản hồi khi chưa gửi header/body, tránh ghi đè phản hồi đang truyền.
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            LogUnexpectedException(logger, context.Request.Path, exception);
            await GatewayErrorResponseWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                GatewayError.UnexpectedFailure,
                context.RequestAborted);
        }
    }
}
