using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IAggregateWatermarkRepository
{
    Task AcquireAdvisoryLockAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        string scope,
        Guid aggregateId,
        Guid recipientUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        string scope,
        Guid aggregateId,
        CancellationToken cancellationToken = default);

    Task UpsertWatermarkAsync(
        NotificationAggregateWatermark watermark,
        CancellationToken cancellationToken = default);

    Task UpsertWatermarksForRecipientsAsync(
        string scope,
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        bool isPaid = false,
        Guid? parentOrderId = null,
        CancellationToken cancellationToken = default);
}
