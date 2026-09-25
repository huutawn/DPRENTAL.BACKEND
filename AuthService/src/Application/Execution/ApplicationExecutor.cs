using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Application.Exceptions;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Execution;

/// <summary>
/// Cơ chế thực thi chung tại ranh giới Application:
/// - Kiểm tra CancellationToken và rethrow OperationCanceledException.
/// - Ánh xạ vi phạm ràng buộc DB đã nhận diện chính xác thành lỗi dự kiến (409 Conflict).
/// - Exception còn lại được log một lần với tên thao tác và correlation ID (không ghi secret/token) rồi trả Auth.Uncategorized (500).
/// </summary>
[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "General application boundary logger.")]
public static class ApplicationExecutor
{
    public static async Task<Result<T>> ExecuteAsync<T>(
        string operationName,
        Func<Task<Result<T>>> action,
        ILogger logger,
        ICorrelationIdProvider correlationIdProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(correlationIdProvider);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is StorageConstraintViolationException constraint && StorageConstraintMapping.TryMap(constraint, out Error? expectedError))
            {
                return Result.Failure<T>(expectedError);
            }

            string corrId = correlationIdProvider.GetCorrelationId();
            logger.LogError(
                "Unexpected exception during {OperationName}. CorrelationId: {CorrelationId}, ExceptionType: {ExceptionType}, Constraint: {ConstraintName}, StackTrace: {StackTrace}",
                operationName,
                corrId,
                ex.GetType().FullName,
                (ex as StorageConstraintViolationException)?.ConstraintName,
                ex.StackTrace);

            return Result.Failure<T>(AuthErrors.Auth.Uncategorized);
        }
    }

    public static async Task<Result> ExecuteAsync(
        string operationName,
        Func<Task<Result>> action,
        ILogger logger,
        ICorrelationIdProvider correlationIdProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(correlationIdProvider);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is StorageConstraintViolationException constraint && StorageConstraintMapping.TryMap(constraint, out Error? expectedError))
            {
                return Result.Failure(expectedError);
            }

            string corrId = correlationIdProvider.GetCorrelationId();
            logger.LogError(
                "Unexpected exception during {OperationName}. CorrelationId: {CorrelationId}, ExceptionType: {ExceptionType}, Constraint: {ConstraintName}, StackTrace: {StackTrace}",
                operationName,
                corrId,
                ex.GetType().FullName,
                (ex as StorageConstraintViolationException)?.ConstraintName,
                ex.StackTrace);

            return Result.Failure(AuthErrors.Auth.Uncategorized);
        }
    }
}
