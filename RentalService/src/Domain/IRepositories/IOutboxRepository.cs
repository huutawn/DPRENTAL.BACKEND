using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public interface IOutboxRepository
{
    Task<IReadOnlyList<RentalOutboxMessage>> ClaimPendingBatchAsync(
        string claimToken,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task AddAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default);

    Task<bool> MarkPublishedAsync(
        Guid id,
        string claimToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    Task<bool> RecordFailureAsync(
        Guid id,
        string claimToken,
        string errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextScheduledAt = null,
        CancellationToken cancellationToken = default);
}
