namespace Dprental.Gateway.Errors;

/// <summary>Writes the one canonical Gateway error representation without exposing implementation details.</summary>
internal static class GatewayErrorResponseWriter
{
    /// <summary>Writes an error only while an HTTP response can still be safely changed.</summary>
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
        context.Response.ContentLength = null;
        return context.Response.WriteAsJsonAsync(
            new GatewayErrorResponse(error.Code, error.Message, context.TraceIdentifier),
            cancellationToken: cancellationToken);
    }
}
