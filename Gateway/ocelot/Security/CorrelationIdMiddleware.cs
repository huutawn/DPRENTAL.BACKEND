namespace Dprental.Gateway.Security;

/// <summary>Cung cấp mã định danh tương quan (Correlation ID) an toàn cho caller, logs và các downstream services.</summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string HeaderName = "X-Correlation-ID";

    /// <summary>Sử dụng GUID do caller cung cấp nếu hợp lệ, hoặc tạo GUID mới cho request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = Guid.TryParse(context.Request.Headers[HeaderName].FirstOrDefault(), out Guid parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        // Đồng bộ tiêu đề trên request để đảm bảo giá trị luôn là một GUID hợp lệ
        context.Request.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
