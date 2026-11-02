namespace Dprental.NotificationService.Application.IServices;

public interface INotificationRealtimeNotifier
{
    Task NotifyInboxChangedAsync(
        Guid userId,
        Guid notificationId,
        string changeType,
        string category,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
