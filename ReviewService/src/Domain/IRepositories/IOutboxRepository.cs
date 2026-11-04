using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid messageId, DateTimeOffset processedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(string claimOwner, TimeSpan leaseDuration, int batchSize, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task FailAttemptAsync(Guid messageId, string errorCode, DateTimeOffset nextAttempt, bool park = false, CancellationToken cancellationToken = default);
}
