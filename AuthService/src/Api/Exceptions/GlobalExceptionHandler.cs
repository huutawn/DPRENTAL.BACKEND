using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AuthService.Api.Exceptions;

/// <summary>
/// Xử lý exception toàn cục tại API boundary:
/// - Ánh xạ StorageConstraintViolationException đã nhận diện sang 409 Conflict ProblemDetails.
/// - Các exception còn lại log một lần với CorrelationId, không để lộ thông tin nhạy cảm và trả về 500 ProblemDetails (Auth.Uncategorized).
/// </summary>
[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Global boundary exception handler logger.")]
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        int statusCode;
        string title;
        string detail;
        string type;

        if (exception is StorageConstraintViolationException constraint && StorageConstraintMapping.TryMap(constraint, out Error? expectedError))
        {
            statusCode = StatusCodes.Status409Conflict;
            title = expectedError.Code;
            detail = expectedError.Message;
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.10";
        }
        else
        {
            string corrId = httpContext.RequestServices.GetService<ICorrelationIdProvider>()?.GetCorrelationId() ?? "unknown";
            logger.LogError(
                "Unexpected exception during {Path}. CorrelationId: {CorrelationId}, ExceptionType: {ExceptionType}, Constraint: {ConstraintName}, StackTrace: {StackTrace}",
                httpContext.Request.Path.Value ?? "unknown",
                corrId,
                exception.GetType().FullName,
                (exception as StorageConstraintViolationException)?.ConstraintName,
                exception.StackTrace);

            statusCode = StatusCodes.Status500InternalServerError;
            title = AuthErrors.Auth.Uncategorized.Code;
            detail = AuthErrors.Auth.Uncategorized.Message;
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.1";
        }

        httpContext.Response.StatusCode = statusCode;

        ProblemDetails problemDetails = new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type
        };

        bool written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });

        if (!written)
        {
            httpContext.Response.ContentType = "application/problem+json";
            await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        }

        return true;
    }
}
