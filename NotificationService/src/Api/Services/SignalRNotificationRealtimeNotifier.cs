using Dprental.NotificationService.Api.Hubs;
using Dprental.NotificationService.Application.IServices;
using Microsoft.AspNetCore.SignalR;

namespace Dprental.NotificationService.Api.Services;

public sealed class SignalRNotificationRealtimeNotifier : INotificationRealtimeNotifier
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<SignalRNotificationRealtimeNotifier> _logger;

    public SignalRNotificationRealtimeNotifier(
        IHubContext<NotificationHub> hubContext,
        ILogger<SignalRNotificationRealtimeNotifier> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task NotifyInboxChangedAsync(
        Guid userId,
        Guid notificationId,
        string changeType,
        string category,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var groupName = $"user:{userId}";
        var payload = new
        {
            notificationId,
            changeType,
            category,
            occurredAt
        };

        try
        {
            await _hubContext.Clients.Group(groupName).SendAsync("NotificationInboxChanged", payload, cancellationToken);
            _logger.LogInformation("Đã phát sự kiện NotificationInboxChanged tới {Group} cho NotificationId={NotificationId}", groupName, notificationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi phát sự kiện NotificationInboxChanged tới {Group}", groupName);
        }
    }
}
