using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<bool> MarkProcessedAsync(Guid messageId, Guid claimToken, string claimOwner, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(string claimOwner, TimeSpan leaseDuration, int batchSize, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> FailAttemptAsync(Guid messageId, Guid claimToken, string claimOwner, string errorCode, DateTimeOffset nextAttempt, bool park, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
