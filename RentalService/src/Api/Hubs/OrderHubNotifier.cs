using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Hubs;

/// <summary>
/// Cài đặt IOrderHubNotifier sử dụng IHubContext gửi thông báo invalidation thời gian thực.
/// Gửi có timeout hữu hạn, không fire-and-forget; lỗi push hoặc timeout được ghi log và không làm fail business mutation.
/// </summary>
public sealed partial class OrderHubNotifier(
    IHubContext<OrderHub> hubContext,
    ILogger<OrderHubNotifier> logger) : IOrderHubNotifier
{
    private readonly IHubContext<OrderHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger<OrderHubNotifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private static readonly TimeSpan DefaultPushTimeout = TimeSpan.FromSeconds(5);

    public async Task NotifyRenterOrderChangedAsync(
        Guid renterId,
        RenterOrderChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (renterId == Guid.Empty) return;

        string group = OrderHubGroupNames.Renter(renterId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultPushTimeout);

        try
        {
            await _hubContext.Clients.Group(group).SendAsync("RenterOrderChanged", notification, cts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushTimeout(_logger, group, notification.OrderId, notification.ChangeType, ex);
        }
        catch (Exception ex)
        {
            LogPushFailed(_logger, group, notification.OrderId, notification.ChangeType, ex);
        }
    }

    public async Task NotifySupplierSubOrderChangedAsync(
        Guid supplierId,
        SupplierSubOrderChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (supplierId == Guid.Empty) return;

        string group = OrderHubGroupNames.Supplier(supplierId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultPushTimeout);

        try
        {
            await _hubContext.Clients.Group(group).SendAsync("SupplierSubOrderChanged", notification, cts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogPushTimeout(_logger, group, notification.OrderId, notification.ChangeType, ex);
        }
        catch (Exception ex)
        {
            LogPushFailed(_logger, group, notification.OrderId, notification.ChangeType, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Push notification timed out for group {Group}, Order {OrderId}, ChangeType {ChangeType}.")]
    private static partial void LogPushTimeout(ILogger logger, string group, Guid orderId, string changeType, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to push notification to group {Group}, Order {OrderId}, ChangeType {ChangeType}.")]
    private static partial void LogPushFailed(ILogger logger, string group, Guid orderId, string changeType, Exception ex);
}
