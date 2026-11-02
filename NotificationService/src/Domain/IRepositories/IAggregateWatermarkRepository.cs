using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IAggregateWatermarkRepository
{
    Task<NotificationAggregateWatermark?> GetWatermarkAsync(Guid aggregateId, Guid recipientUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(Guid aggregateId, CancellationToken cancellationToken = default);
    Task UpsertWatermarkAsync(NotificationAggregateWatermark watermark, CancellationToken cancellationToken = default);
    Task UpsertWatermarksForRecipientsAsync(Guid aggregateId, IEnumerable<Guid> recipientUserIds, long orderVersion, string? messageId, bool isTerminal, string? terminalState, CancellationToken cancellationToken = default);
}
