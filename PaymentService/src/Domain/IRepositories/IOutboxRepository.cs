using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepositories;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        string workerId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<bool> MarkPublishedAsync(
        Guid id,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        Guid id,
        string workerId,
        string errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default);

    Task<bool> DeferMessageAsync(
        Guid id,
        string workerId,
        DateTimeOffset nextRetryAt,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<bool> QuarantineMessageAsync(
        Guid id,
        string workerId,
        string reason,
        DateTimeOffset quarantinedAt,
        CancellationToken cancellationToken = default);
}
