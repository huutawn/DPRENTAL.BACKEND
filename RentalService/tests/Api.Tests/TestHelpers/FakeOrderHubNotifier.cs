using System.Collections.Concurrent;
using Dprental.RentalService.Application.Hubs;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public sealed class FakeOrderHubNotifier : IOrderHubNotifier
{
    public ConcurrentQueue<(Guid RenterId, RenterOrderChangedNotification Notification)> RenterNotifications { get; } = new();
    public ConcurrentQueue<(Guid SupplierId, SupplierSubOrderChangedNotification Notification)> SupplierNotifications { get; } = new();

    public bool ThrowOnPush { get; set; }
    public bool TimeoutOnPush { get; set; }

    public Task NotifyRenterOrderChangedAsync(
        Guid renterId,
        RenterOrderChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (TimeoutOnPush)
        {
            throw new OperationCanceledException("Push notification timed out.");
        }
        if (ThrowOnPush)
        {
            throw new InvalidOperationException("SignalR connection failed.");
        }

        RenterNotifications.Enqueue((renterId, notification));
        return Task.CompletedTask;
    }

    public Task NotifySupplierSubOrderChangedAsync(
        Guid supplierId,
        SupplierSubOrderChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (TimeoutOnPush)
        {
            throw new OperationCanceledException("Push notification timed out.");
        }
        if (ThrowOnPush)
        {
            throw new InvalidOperationException("SignalR connection failed.");
        }

        SupplierNotifications.Enqueue((supplierId, notification));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        RenterNotifications.Clear();
        SupplierNotifications.Clear();
        ThrowOnPush = false;
        TimeoutOnPush = false;
    }
}
