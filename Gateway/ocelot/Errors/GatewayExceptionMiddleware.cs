namespace Dprental.Gateway.Errors;

/// <summary>Converts unexpected Gateway exceptions to the canonical error response and retains diagnostic detail in logs.</summary>
internal sealed class GatewayExceptionMiddleware(RequestDelegate next, ILogger<GatewayExceptionMiddleware> logger)
{
    private static readonly Action<ILogger, PathString, Exception?> LogUnexpectedException = LoggerMessage.Define<PathString>(
        LogLevel.Error,
        new EventId(1, "UnhandledGatewayException"),
        "Unhandled Gateway exception for {RequestPath}.");

    /// <summary>Executes the next middleware and translates only unhandled server-side failures.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
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
