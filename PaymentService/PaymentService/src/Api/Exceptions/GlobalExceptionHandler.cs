using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Exceptions;

/// <summary>Handles unexpected failures without exposing exception messages or sensitive payloads.</summary>
[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Global exception boundary.")]
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (httpContext.Response.HasStarted ||
            exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        logger.LogError("Unexpected exception {ExceptionType}. TraceId: {TraceId}",
            exception.GetType().FullName, traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "PaymentService.Unexpected",
            Detail = "An unexpected error occurred.",
            Type = "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1"
        };
        problem.Extensions["traceId"] = traceId;

        bool written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        });
        if (!written)
        {
            await httpContext.Response.WriteAsJsonAsync(problem,
                options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        }

        return true;
    }
}
