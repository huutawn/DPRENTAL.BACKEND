using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

public interface IOutboxRepository
{
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        string workerId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        int batchSize,
        int maxRetries = 10,
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
        bool isExhausted,
        CancellationToken cancellationToken = default);
}
