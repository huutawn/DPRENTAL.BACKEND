using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record OutboxEnqueueResult(
    RentalOutboxMessage Message,
    bool IsReplay);

public interface IOutboxRepository
{
    Task<RentalOutboxMessage?> ClaimNextPendingAsync(
        string claimToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);


    Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(
        RentalOutboxMessage message,
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
