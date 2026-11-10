using Dprental.RentalService.Application.IServices.Cancellation;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Overdue;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

public sealed class CancellationExpiryAndOverdueNotificationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateMultiSupplierOrder(Guid orderId, Guid renterId, Guid supplierA, Guid supplierB, bool paid = false)
    {
        var unitA = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemA = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
            new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25),
            "Máy xúc 01", "Desc", "Kho A", 1_000_000m, 5_000_000m,
            "grp-A", [unitA],
            TestListingSnapshots.Default);

        var unitB = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemB = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
            new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 25),
            "Máy đầm 02", "Desc", "Kho B", 500_000m, 2_000_000m,
            "grp-B", [unitB],
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId, "DH-2026-MULTI-EXP", renterId, "VND", "Pickup",
            "Nguyễn Văn An", "0901234567", "123 Đường A", null,
            "v1.0", "terms-doc", "terms-hash", "op-create", FixedNow,
            [initialItemA, initialItemB]).Value;

        if (paid)
        {
            order.MarkAsPaid("INV-PAID-01", "PAY-PAID-01", FixedNow, FixedNow);
        }

        return order;
    }

    [Fact]
    public async Task RenterCancelOrder_WhenPendingPayment_EmitsOrderCancelled_ToRenterOnly_AndNeverToSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);
        var kafkaOptions = Options.Create(new KafkaOptions());

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierOrder(orderId, renterId, supplierA, supplierB, paid: false); // Unpaid!
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id,
            RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString()),
            "CreateInvoice", "create-invoice-fingerprint", false, null, null,
            FixedNow, RentalOperationReceipt.StatusPending));
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var executor = new RentalOperationExecutor(
            orders, receipts, outboxes, lockManager,
            new Mock<IInventoryRentalClient>().Object,
            new Mock<IPaymentRentalClient>().Object,
            cmdProducer, db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            null, null, timeProvider, eventProducer, notifier);

        var cancellationService = new RenterOrderCancellationService(executor);
        var caller = OrderTestData.User(renterId);

        // Cancel order while PendingPayment
        var cancelResult = await cancellationService.CancelOrderAsync(
            orderId, new CancelOrderRequest("Đổi ý không thuê nữa"), "idemp-cancel-1", caller);

        Assert.True(cancelResult.IsSuccess);

        // 1. Renter receives ORDER_CANCELLED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.OrderCancelled, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Suppliers receive NOTHING (unpaid orders are never visible to suppliers)
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task PaymentExpiry_EmitsPaymentExpired_ToRenterOnly_AndNeverToSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow.AddHours(2)); // Beyond payment deadline
        var kafkaOptions = Options.Create(new KafkaOptions());

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierOrder(orderId, renterId, supplierA, supplierB, paid: false);
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var executor = new RentalOperationExecutor(
            orders, receipts, outboxes, lockManager,
            new Mock<IInventoryRentalClient>().Object,
            new Mock<IPaymentRentalClient>().Object,
            cmdProducer, db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            null, null, timeProvider, eventProducer, notifier);

        // Execute payment expiry
        var expiryResult = await executor.ExecutePaymentExpiryAsync(orderId, "op-pay-expiry-1");
        Assert.True(expiryResult.IsSuccess);

        // 1. Renter receives PAYMENT_EXPIRED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.PaymentExpired, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Suppliers receive NOTHING
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task RenterCancelItem_PaidOrder_EmitsItemCancelled_ToRenterAndOnlyImpactedSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);
        var kafkaOptions = Options.Create(new KafkaOptions());

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierOrder(orderId, renterId, supplierA, supplierB, paid: true);
        var itemA = order.Items.Single(i => i.SupplierId == supplierA);
        itemA.ApplySupplierAccept(supplierA, FixedNow, "conf-A", "conf-A");

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var executor = new RentalOperationExecutor(
            orders, receipts, outboxes, lockManager,
            new Mock<IInventoryRentalClient>().Object,
            new Mock<IPaymentRentalClient>().Object,
            cmdProducer, db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            null, null, timeProvider, eventProducer, notifier);

        var itemCancelService = new ItemCancellationService(
            orders, lockManager, executor, outboxes, db, timeProvider,
            NullLogger<ItemCancellationService>.Instance, null, eventProducer, notifier);

        var caller = OrderTestData.User(renterId);

        // Cancel Item A (not delivered)
        var cancelResult = await itemCancelService.CancelByRenterAsync(
            itemA.Id, new CancelNotDeliveredRequest("Không giao được máy"), "cancel-item-1", caller);

        Assert.True(cancelResult.IsSuccess);

        // 1. Renter receives ITEM_CANCELLED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.ItemCancelled, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A receives ITEM_CANCELLED for their suborder
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.ItemCancelled, supplierNote.Notification.ChangeType);

        // 3. Supplier B receives NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task SupplierConfirmationExpiry_EmitsSupplierConfirmationExpired_ToRenterAndOnlyImpactedSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var inboxes = new InboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow.AddHours(25)); // Past 24h confirmation deadline
        var kafkaOptions = Options.Create(new KafkaOptions());

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierOrder(orderId, renterId, supplierA, supplierB, paid: true);
        var itemA = order.Items.Single(i => i.SupplierId == supplierA);
        order.AttachInvoice("INV-PAID-01", FixedNow);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var executor = new RentalOperationExecutor(
            orders, receipts, outboxes, lockManager,
            new Mock<IInventoryRentalClient>().Object,
            new Mock<IPaymentRentalClient>().Object,
            cmdProducer, db,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            null, null, timeProvider, eventProducer, notifier);

        var expiryWorkflow = new ExpiryWorkflowService(
            orders, outboxes, lockManager, executor, db,
            new ConfigurationBuilder().Build(),
            NullLogger<ExpiryWorkflowService>.Instance,
            timeProvider, eventProducer, notifier);

        // Expire Item A supplier confirmation timeout
        var res = await expiryWorkflow.ExpireSingleItemSupplierTimeoutAsync(itemA.Id);
        Assert.True(res.IsSuccess);

        // 1. Renter receives SUPPLIER_CONFIRMATION_EXPIRED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.SupplierConfirmationExpired, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A receives SUPPLIER_CONFIRMATION_EXPIRED
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.SupplierConfirmationExpired, supplierNote.Notification.ChangeType);

        // 3. Supplier B receives NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

}
