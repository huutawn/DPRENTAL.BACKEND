using Dprental.NotificationService.Application.DTOs;

namespace Dprental.NotificationService.Application.IServices;

public interface INotificationRealtimeNotifier
{
    Task NotifyInboxChangedAsync(
        Guid userId,
        NotificationInboxChangedEvent changeEvent,
        CancellationToken cancellationToken = default);

    Task NotifyInboxChangedAsync(
        Guid userId,
        Guid? notificationId,
        string changeType,
        string? category,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
