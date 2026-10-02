namespace Dprental.CatalogService.Api.Observability;

/// <summary>
/// Gắn correlation ID cho mỗi request: nhận <c>X-Correlation-ID</c> từ Gateway hoặc sinh mới,
/// trả lại trên response và đưa vào log scope để mọi dòng log của request đều mang ID này.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Tên extension chứa correlation ID trong ProblemDetails.</summary>
    public const string ProblemDetailsKey = "correlationId";

    /// <summary>Giới hạn độ dài để header do client gửi không làm phình log.</summary>
    public const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? requested = context.Request.Headers[CorrelationIdProvider.HeaderName].FirstOrDefault();
        string correlationId = IsValid(requested) ? requested! : Guid.NewGuid().ToString();

        context.Items[CorrelationIdProvider.ItemKey] = correlationId;
        context.Response.Headers[CorrelationIdProvider.HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>Correlation ID đã gán cho request; "unknown" nếu middleware chưa chạy.</summary>
    public static string GetCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(CorrelationIdProvider.ItemKey, out object? value) && value is string correlationId
            ? correlationId
            : "unknown";
    }

    /// <summary>
    /// Header đến từ bên ngoài và được ghi vào log, nên chỉ chấp nhận chữ, số và <c>- _ .</c>
    /// để tránh log injection; giá trị không hợp lệ được thay bằng ID mới.
    /// </summary>
    internal static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
