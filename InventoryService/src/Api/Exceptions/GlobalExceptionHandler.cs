using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Exceptions;

/// <summary>Xử lý lỗi ngoài dự kiến; không đưa nội dung exception vào response hoặc log.</summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, Exception?> LogUnexpected = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(1, "UnexpectedException"),
        "Unexpected exception {ExceptionType}. TraceId: {TraceId}");

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return false;

        LogUnexpected(logger, exception.GetType().FullName ?? exception.GetType().Name,
            httpContext.TraceIdentifier, null);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Inventory.Unexpected",
            Detail = "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.",
            Type = "https://www.rfc-editor.org/rfc/rfc9110.html#section-15.6.1"
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (!await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        { HttpContext = httpContext, ProblemDetails = problem }))
        {
            await httpContext.Response.WriteAsJsonAsync(problem, options: null,
                contentType: "application/problem+json", cancellationToken: cancellationToken);
        }
        return true;
    }
}
