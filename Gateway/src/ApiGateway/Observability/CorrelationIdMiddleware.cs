namespace Dprental.Gateway.Observability;

/// <summary>Supplies a safe request correlation identifier to callers, logs, and downstream services.</summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string HeaderName = "X-Correlation-ID";

    /// <summary>Uses a caller-provided GUID or creates a fresh identifier for the request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = Guid.TryParse(context.Request.Headers[HeaderName].FirstOrDefault(), out Guid parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
