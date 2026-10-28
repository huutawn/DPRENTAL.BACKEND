using System.Text.Json;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

public sealed class OrderHubNotifierTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NotifyRenterOrderChangedAsync_PushesToCorrectGroup_WithExactContractFields()
    {
        Guid renterId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset occurredAt = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

        var notification = new RenterOrderChangedNotification(
            eventId,
            orderId,
            18,
            OrderChangeTypes.OrderCreated,
            occurredAt);

        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockHubContext = new Mock<IHubContext<OrderHub>>();

        string expectedGroup = $"renter:{renterId:D}";
        mockClients.Setup(c => c.Group(expectedGroup)).Returns(mockClientProxy.Object);
        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);

        var notifier = new OrderHubNotifier(
            mockHubContext.Object,
            NullLogger<OrderHubNotifier>.Instance);

        await notifier.NotifyRenterOrderChangedAsync(renterId, notification, CancellationToken.None);

        mockClientProxy.Verify(p => p.SendCoreAsync(
            "RenterOrderChanged",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], notification)),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify JSON contract
        string json = JsonSerializer.Serialize(notification, JsonOptions);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(eventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(orderId, root.GetProperty("orderId").GetGuid());
        Assert.Equal(18, root.GetProperty("version").GetInt64());
        Assert.Equal(OrderChangeTypes.OrderCreated, root.GetProperty("changeType").GetString());
        Assert.Equal("2026-10-26T10:00:00+00:00", root.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task NotifySupplierSubOrderChangedAsync_PushesToCorrectGroup_WithExactContractFields()
    {
        Guid supplierId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid subOrderId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset occurredAt = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

        var notification = new SupplierSubOrderChangedNotification(
            eventId,
            orderId,
            subOrderId,
            18,
            OrderChangeTypes.PaymentConfirmed,
            occurredAt);

        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockHubContext = new Mock<IHubContext<OrderHub>>();

        string expectedGroup = $"supplier:{supplierId:D}";
        mockClients.Setup(c => c.Group(expectedGroup)).Returns(mockClientProxy.Object);
        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);

        var notifier = new OrderHubNotifier(
            mockHubContext.Object,
            NullLogger<OrderHubNotifier>.Instance);

        await notifier.NotifySupplierSubOrderChangedAsync(supplierId, notification, CancellationToken.None);

        mockClientProxy.Verify(p => p.SendCoreAsync(
            "SupplierSubOrderChanged",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], notification)),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify JSON contract
        string json = JsonSerializer.Serialize(notification, JsonOptions);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(eventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(orderId, root.GetProperty("orderId").GetGuid());
        Assert.Equal(subOrderId, root.GetProperty("subOrderId").GetGuid());
        Assert.Equal(18, root.GetProperty("orderVersion").GetInt64());
        Assert.Equal(OrderChangeTypes.PaymentConfirmed, root.GetProperty("changeType").GetString());
        Assert.Equal("2026-10-26T10:00:00+00:00", root.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task Notify_WhenSignalRExceptionOccurs_LogsAndDoesNotThrow()
    {
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockHubContext = new Mock<IHubContext<OrderHub>>();

        mockClientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SignalR network drop"));

        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);

        var notifier = new OrderHubNotifier(mockHubContext.Object, NullLogger<OrderHubNotifier>.Instance);

        var notification = new RenterOrderChangedNotification(
            Guid.NewGuid(), Guid.NewGuid(), 1, OrderChangeTypes.OrderCreated, DateTimeOffset.UtcNow);

        // Must not throw exception
        var exception = await Record.ExceptionAsync(() =>
            notifier.NotifyRenterOrderChangedAsync(Guid.NewGuid(), notification, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Notify_WhenEmptyIdOrNull_DoesNotThrow()
    {
        var mockHubContext = new Mock<IHubContext<OrderHub>>();
        var notifier = new OrderHubNotifier(mockHubContext.Object, NullLogger<OrderHubNotifier>.Instance);

        var notification = new RenterOrderChangedNotification(
            Guid.NewGuid(), Guid.NewGuid(), 1, OrderChangeTypes.OrderCreated, DateTimeOffset.UtcNow);

        // Empty renter ID is safely ignored
        await notifier.NotifyRenterOrderChangedAsync(Guid.Empty, notification, CancellationToken.None);

        var supplierNotification = new SupplierSubOrderChangedNotification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, OrderChangeTypes.OrderCreated, DateTimeOffset.UtcNow);

        await notifier.NotifySupplierSubOrderChangedAsync(Guid.Empty, supplierNotification, CancellationToken.None);
    }
}
