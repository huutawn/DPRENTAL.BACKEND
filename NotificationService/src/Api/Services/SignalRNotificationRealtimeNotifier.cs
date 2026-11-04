using Dprental.NotificationService.Api.Hubs;
using Dprental.NotificationService.Application.DTOs;
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
        NotificationInboxChangedEvent changeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeEvent);

        var groupName = $"user:{userId}";

        try
        {
            await _hubContext.Clients.Group(groupName).SendAsync("NotificationInboxChanged", changeEvent, cancellationToken);
            _logger.LogInformation(
                "Đã phát sự kiện NotificationInboxChanged tới {Group} cho ChangeType={ChangeType}, NotificationId={NotificationId}",
                groupName, changeEvent.ChangeType, changeEvent.NotificationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi phát sự kiện NotificationInboxChanged tới {Group}", groupName);
        }
    }

    public Task NotifyInboxChangedAsync(
        Guid userId,
        Guid? notificationId,
        string changeType,
        string? category,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        return NotifyInboxChangedAsync(
            userId,
            new NotificationInboxChangedEvent(changeType, notificationId, category, occurredAt),
            cancellationToken);
    }
}
