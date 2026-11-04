using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IAggregateWatermarkRepository
{
    Task AcquireAdvisoryLockAsync(Guid aggregateId, CancellationToken cancellationToken = default);

    Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        Guid aggregateId,
        Guid recipientUserId,
        string scope = NotificationWatermarkScope.RentalOrder,
        CancellationToken cancellationToken = default);

    Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        string scope,
        Guid aggregateId,
        Guid recipientUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        Guid aggregateId,
        string scope = NotificationWatermarkScope.RentalOrder,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        string scope,
        Guid aggregateId,
        CancellationToken cancellationToken = default);

    Task UpsertWatermarkAsync(
        NotificationAggregateWatermark watermark,
        CancellationToken cancellationToken = default);

    Task UpsertWatermarksForRecipientsAsync(
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        string scope = NotificationWatermarkScope.RentalOrder,
        bool isPaid = false,
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
        CancellationToken cancellationToken = default);

    Task UpsertWatermarksAsync(
        string scope,
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        bool isPaid = false,
        CancellationToken cancellationToken = default);
}
