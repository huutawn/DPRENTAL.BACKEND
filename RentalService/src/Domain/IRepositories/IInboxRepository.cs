using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record InboxMessageDescriptor(
    Guid Id,
    Guid MessageId,
    string MessageType,
    string AggregateId,
    string Status,
    int RetryCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset ReceivedAt);

public interface IInboxRepository
{
    Task<RentalInboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task<RentalInboxMessage?> GetByIdForProcessingAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboxMessageDescriptor>> GetPendingRetryDescriptorsAsync(
        DateTimeOffset utcNow,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task AddAsync(RentalInboxMessage message, CancellationToken cancellationToken = default);

    void Update(RentalInboxMessage message);
}
