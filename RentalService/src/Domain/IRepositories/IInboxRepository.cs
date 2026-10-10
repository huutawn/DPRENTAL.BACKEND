using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public interface IInboxRepository
{
    Task<RentalInboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalInboxMessage>> GetPendingRetryBatchAsync(
        DateTimeOffset utcNow,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task AddAsync(RentalInboxMessage message, CancellationToken cancellationToken = default);

    void Update(RentalInboxMessage message);
}
