using System.Diagnostics.CodeAnalysis;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Exceptions;

/// <summary>
/// Xử lý exception toàn cục tại API boundary:
/// - Request bị hủy không bị chuyển thành lỗi nghiệp vụ.
/// - Các exception còn lại log một lần với CorrelationId, không để lộ thông tin nhạy cảm và trả về 500 ProblemDetails (Catalog.Uncategorized).
/// </summary>
[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Global boundary exception handler logger.")]
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
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

        string corrId = httpContext.RequestServices.GetService<ICorrelationIdProvider>()?.GetCorrelationId() ?? "unknown";
        logger.LogError(
            "Unexpected exception during {Path}. CorrelationId: {CorrelationId}, ExceptionType: {ExceptionType}, StackTrace: {StackTrace}",
            httpContext.Request.Path.Value ?? "unknown",
            corrId,
            exception.GetType().FullName,
            exception.StackTrace);

        const int statusCode = StatusCodes.Status500InternalServerError;
        string title = CatalogErrors.Catalog.Uncategorized.Code;
        string detail = CatalogErrors.Catalog.Uncategorized.Message;
        const string type = "https://tools.ietf.org/html/rfc9110#section-15.6.1";

        httpContext.Response.StatusCode = statusCode;

        ProblemDetails problemDetails = new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

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
