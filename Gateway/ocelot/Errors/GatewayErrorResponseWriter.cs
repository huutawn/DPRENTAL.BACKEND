namespace Dprental.Gateway.Errors;

/// <summary>Ghi phản hồi lỗi theo cấu trúc chung, không đưa chi tiết triển khai nội bộ ra client.</summary>
internal static class GatewayErrorResponseWriter
{
    /// <summary>Chỉ ghi lỗi khi phản hồi HTTP chưa bắt đầu gửi về client.</summary>
    internal static Task WriteAsync(
        HttpContext context,
        int statusCode,
        GatewayError error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);

        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        // Xóa độ dài cũ vì nội dung lỗi JSON có thể khác nội dung phản hồi trước đó.
        context.Response.ContentLength = null;
        return context.Response.WriteAsJsonAsync(
            new GatewayErrorResponse(error.Code, error.Message, context.TraceIdentifier),
            cancellationToken: cancellationToken);
    }
}
