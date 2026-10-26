using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Application.Services.Integration;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Services.Payments;
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

public sealed class OrderNotificationIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateTestOrder(Guid orderId, Guid renterId, Guid supplier1, Guid supplier2)
    {
        var item1 = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplier1, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Máy xúc 01", "Desc", "Kho 1", 1_000_000m, 5_000_000m,
            "grp-1", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        var item2 = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplier2, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Máy đầm 02", "Desc", "Kho 2", 500_000m, 2_000_000m,
            "grp-2", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId, "DH-2026-999001", renterId, "VND", "Pickup",
            "Nguyễn Văn A", "0901234567", "123 Đường A", null,
            "v1.0", "terms-doc", "terms-hash", "op-create", FixedNow,
            [item1, item2]).Value;
    }

    [Fact]
    public async Task AttachInvoice_EmitsInvoiceAttached_OnlyWhenNewlyAttached_AndNotOnReplay()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var operations = new RentalCheckoutOperationRepository(db);
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, Guid.NewGuid(), Guid.NewGuid());
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var mockPayment = new Mock<IPaymentRentalClient>();
        mockPayment.Setup(p => p.CreateInvoiceAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new CreateInvoiceResponse(
                "INV-2026-001", order.TotalPayableAmount, order.Currency,
                order.PaymentDeadline, "Pending", "https://qr.test/pay", "Bank", "123",
                "DH-2026-999001", false)));

        var issuer = new CheckoutInvoiceIssuer(
            mockPayment.Object, orders, operations, db, timeProvider,
            NullLogger<CheckoutInvoiceIssuer>.Instance, notifier);

        // 1. First invocation: attaches invoice and commits
        var res1 = await issuer.EnsureInvoiceAsync(order);
        Assert.True(res1.IsSuccess);
        Assert.Single(notifier.RenterNotifications);
        var note1 = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, note1.RenterId);
        Assert.Equal(orderId, note1.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.InvoiceAttached, note1.Notification.ChangeType);
        Assert.Equal(order.Version, note1.Notification.Version);

        // 2. Replay / duplicate invocation: invoice already attached
        notifier.Reset();
        var res2 = await issuer.EnsureInvoiceAsync(order);
        Assert.True(res2.IsSuccess);
        Assert.Empty(notifier.RenterNotifications); // No duplicate notification!
    }

    [Fact]
    public async Task PaymentConfirmed_EmitsRenterAndSubOrderSupplierNotifications_AfterCommit_AndNotOnDuplicate()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, supplierA, supplierB);
        order.AttachInvoice("INV-TEST-999", FixedNow);
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var invoicePaidPayload = new InvoicePaidPayload(
            orderId, renterId, "INV-TEST-999", "PAY-TEST-999",
            order.TotalPayableAmount, order.Currency, FixedNow);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
            orderId.ToString("D"), FixedNow, "corr-1", null, invoicePaidPayload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, "InvoicePaid",
            "payment-service", orderId.ToString("D"), "corr-1", null,
            rawJson, FixedNow, "fp-1");
        inboxMsg.AssignPublicationProof(
            PaymentPublicationProofTestFixture.Create(rawJson),
            orderId.ToString("D"));

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            RentalEventsTopic = "rental.events.v1"
        }));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var mockExecutor = new Mock<IRentalOperationExecutor>();

        var workflowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:RentalEventsTopic"] = "rental.events.v1"
        }).Build();

        var paymentWorkflow = new PaymentWorkflowService(
            orders, inboxes, outboxes, receipts, lockManager,
            mockExecutor.Object, db, cmdProducer, workflowConfig,
            NullLogger<PaymentWorkflowService>.Instance, eventProducer, timeProvider, notifier);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager, paymentWorkflow,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            new Mock<IInventoryAllocationResultHandler>().Object,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null,
            null,
            notifier);

        // 1. Process payment first time -> Success & commit
        var res1 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res1.IsSuccess);

        // Verify Renter received notification
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.PaymentConfirmed, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // Verify each supplier received notification for their own suborder
        Assert.Equal(2, notifier.SupplierNotifications.Count);
        var subOrders = order.SubOrders.ToList();

        var noteA = notifier.SupplierNotifications.Single(n => n.SupplierId == supplierA);
        Assert.Equal(orderId, noteA.Notification.OrderId);
        Assert.Equal(subOrders.Single(s => s.SupplierId == supplierA).Id, noteA.Notification.SubOrderId);
        Assert.Equal(order.Version, noteA.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.PaymentConfirmed, noteA.Notification.ChangeType);

        var noteB = notifier.SupplierNotifications.Single(n => n.SupplierId == supplierB);
        Assert.Equal(orderId, noteB.Notification.OrderId);
        Assert.Equal(subOrders.Single(s => s.SupplierId == supplierB).Id, noteB.Notification.SubOrderId);
        Assert.Equal(order.Version, noteB.Notification.OrderVersion);

        // 2. Duplicate payment event processing -> no duplicate notification
        notifier.Reset();
        var res2 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res2.IsSuccess);
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task DeliveryNoteUpdated_UnpaidOrder_NotifiesOnlyRenter_PaidOrder_NotifiesRenterAndSuppliers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, supplierId, Guid.NewGuid());
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var deliveryService = new RenterDeliveryNoteService(orders, db, timeProvider, notifier);
        var caller = OrderTestData.User(renterId);

        // 1. Unpaid order: update note -> only renter notified, supplier receives 0
        var res1 = await deliveryService.UpdateDeliveryNoteAsync(
            orderId, new UpdateDeliveryNoteRequest("Ghi chú trước khi trả tiền"), caller);
        Assert.True(res1.IsSuccess);

        Assert.Single(notifier.RenterNotifications);
        Assert.Equal(OrderChangeTypes.DeliveryNoteUpdated, notifier.RenterNotifications.Single().Notification.ChangeType);
        Assert.Empty(notifier.SupplierNotifications); // Unpaid orders not visible to supplier

        // 2. Mark order as paid
        notifier.Reset();
        order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
        await orders.UpdateAsync(order);
        await db.SaveChangesAsync();

        // 3. Paid order: update note -> renter AND suppliers notified
        var res2 = await deliveryService.UpdateDeliveryNoteAsync(
            orderId, new UpdateDeliveryNoteRequest("Ghi chú sau khi đã trả tiền"), caller);
        Assert.True(res2.IsSuccess);

        Assert.Single(notifier.RenterNotifications);
        Assert.Equal(OrderChangeTypes.DeliveryNoteUpdated, notifier.RenterNotifications.Single().Notification.ChangeType);

        Assert.Equal(2, notifier.SupplierNotifications.Count);
        Assert.All(notifier.SupplierNotifications, s => Assert.Equal(OrderChangeTypes.DeliveryNoteUpdated, s.Notification.ChangeType));
    }

    [Fact]
    public async Task PushTimeoutOrFailure_DoesNotFailCommittedBusinessMutation()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var timeProvider = new FixedTimeProvider(FixedNow);

        var mockHubContext = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<OrderHub>>();
        var mockClients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
        var mockGroup = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();

        // Simulate push timeout / socket crash
        mockGroup.Setup(g => g.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Push timeout"));
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroup.Object);
        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);

        var realNotifierWithFailure = new OrderHubNotifier(mockHubContext.Object, NullLogger<OrderHubNotifier>.Instance);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, Guid.NewGuid(), Guid.NewGuid());
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var deliveryService = new RenterDeliveryNoteService(orders, db, timeProvider, realNotifierWithFailure);
        var caller = OrderTestData.User(renterId);

        // Business mutation MUST succeed even if push fails/times out
        var result = await deliveryService.UpdateDeliveryNoteAsync(
            orderId, new UpdateDeliveryNoteRequest("Cập nhật ghi chú khi push lỗi"), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("Cập nhật ghi chú khi push lỗi", result.Value.Note);

        // Verify database actually persisted the change
        var reloaded = await orders.GetByIdAsync(orderId);
        Assert.Equal("Cập nhật ghi chú khi push lỗi", reloaded?.DeliveryNote);
    }

    [Fact]
    public async Task CheckoutService_SubmitAsync_EmitsOrderCreated_AfterCommit_AndReplayDoesNotDuplicate()
    {
        var harness = new CheckoutTestHarness();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid listingId = harness.AddListing(supplierId, dailyRate: 500_000m);
        var cartItemIds = await harness.SeedCartAsync(renterId, (listingId, 1));
        harness.Auth.UserId = renterId;
        harness.Auth.IsProfileCompleted = true;

        var notifier = new FakeOrderHubNotifier();

        await using var db = harness.NewContext();
        var orders = new RentalOrderRepository(db);
        var carts = new RentalCartRepository(db);
        var operations = new RentalCheckoutOperationRepository(db);
        var outbox = new OutboxRepository(db);
        var inventoryCommands = new InventoryCommandProducer(outbox, Options.Create(new KafkaOptions { Enabled = true }), harness.Clock);
        var orderWriter = new CheckoutOrderWriter(orders, carts, harness.OrderCodes, db, NullLogger<CheckoutOrderWriter>.Instance);
        var rentalEvents = new RentalEventProducer(new KafkaOutboxWriter(outbox), Options.Create(new KafkaOptions { Enabled = true }), harness.Clock);
        var invoiceIssuer = new CheckoutInvoiceIssuer(harness.Payment, orders, operations, db, harness.Clock, NullLogger<CheckoutInvoiceIssuer>.Instance, notifier);

        var service = new CheckoutService(
            new CheckoutPreparationService(
                carts,
                new RenterAddressRepository(db),
                harness.Listings,
                harness.Auth,
                Options.Create(new RentalTermsOptions { CurrentVersion = "v1.0", DocumentRef = "terms", ContentHash = "hash" }),
                harness.Clock),
            operations,
            orders,
            harness.Locks,
            harness.Inventory,
            inventoryCommands,
            orderWriter,
            rentalEvents,
            invoiceIssuer,
            db,
            harness.Clock,
            harness.Payment,
            NullLogger<CheckoutService>.Instance,
            notifier);

        var request = CheckoutTestHarness.Request(expectedTotal: 1_500_000m, [cartItemIds[0]]);
        var renterContext = new CheckoutRenterContext(renterId);

        // 1. First submit: commits order and emits ORDER_CREATED
        var res1 = await service.SubmitAsync(renterContext, request, "idemp-submit-1");
        Assert.True(res1.IsSuccess);
        Guid createdOrderId = res1.Value.OrderId;

        // Verify ORDER_CREATED was emitted for renter
        var orderCreatedNotes = notifier.RenterNotifications
            .Where(n => n.Notification.ChangeType == OrderChangeTypes.OrderCreated)
            .ToList();
        Assert.Single(orderCreatedNotes);
        Assert.Equal(renterId, orderCreatedNotes[0].RenterId);
        Assert.Equal(createdOrderId, orderCreatedNotes[0].Notification.OrderId);
        Assert.Equal(1, orderCreatedNotes[0].Notification.Version);

        // 2. Replay with same idempotency key -> returns cached success, no duplicate ORDER_CREATED
        notifier.Reset();
        var res2 = await service.SubmitAsync(renterContext, request, "idemp-submit-1");
        Assert.True(res2.IsSuccess);
        Assert.True(res2.Value.IsReplay);
        Assert.DoesNotContain(notifier.RenterNotifications, n => n.Notification.ChangeType == OrderChangeTypes.OrderCreated);
    }

    [Fact]
    public async Task Payment_WhenRecoveryPending_EmitsPaymentRecoveryPending_ToRenterOnly_AndNotToSuppliers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, supplierA, Guid.NewGuid());
        order.AttachInvoice("INV-REC-1", FixedNow);
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        // Payment arriving AFTER payment deadline triggers recovery branch (when within recovery cutoff)
        DateTimeOffset latePaidAt = order.PaymentDeadline.AddMinutes(5);
        var invoicePaidPayload = new InvoicePaidPayload(
            orderId, renterId, "INV-REC-1", "PAY-REC-1",
            order.TotalPayableAmount, order.Currency, latePaidAt);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
            orderId.ToString("D"), latePaidAt, "corr-rec", null, invoicePaidPayload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, "InvoicePaid",
            "payment-service", orderId.ToString("D"), "corr-rec", null,
            rawJson, latePaidAt, "fp-rec");
        inboxMsg.AssignPublicationProof(
            PaymentPublicationProofTestFixture.Create(rawJson),
            orderId.ToString("D"));

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            RentalEventsTopic = "rental.events.v1"
        }));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var mockExecutor = new Mock<IRentalOperationExecutor>();

        var workflowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:RentalEventsTopic"] = "rental.events.v1"
        }).Build();

        var paymentWorkflow = new PaymentWorkflowService(
            orders, inboxes, outboxes, receipts, lockManager,
            mockExecutor.Object, db, cmdProducer, workflowConfig,
            NullLogger<PaymentWorkflowService>.Instance, eventProducer, timeProvider, notifier);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager, paymentWorkflow,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            new Mock<IInventoryAllocationResultHandler>().Object,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null,
            null,
            notifier);

        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // Renter receives PAYMENT_RECOVERY_PENDING
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.PaymentRecoveryPending, renterNote.Notification.ChangeType);

        // Suppliers receive NOTHING because visibility is not granted during recovery pending
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task Payment_WhenPastCutoffOrCancelled_EmitsPaymentRefundRequired_ToRenterOnly_AndNotToSuppliers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();

        // Time is way past cutoff (2 hours after deadline)
        DateTimeOffset lateTime = FixedNow.AddHours(2);
        var timeProvider = new FixedTimeProvider(lateTime);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateTestOrder(orderId, renterId, supplierA, Guid.NewGuid());
        order.AttachInvoice("INV-REFUND-1", FixedNow);
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var invoicePaidPayload = new InvoicePaidPayload(
            orderId, renterId, "INV-REFUND-1", "PAY-REFUND-1",
            order.TotalPayableAmount, order.Currency, lateTime);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
            orderId.ToString("D"), lateTime, "corr-ref", null, invoicePaidPayload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, "InvoicePaid",
            "payment-service", orderId.ToString("D"), "corr-ref", null,
            rawJson, lateTime, "fp-ref");
        inboxMsg.AssignPublicationProof(
            PaymentPublicationProofTestFixture.Create(rawJson),
            orderId.ToString("D"));

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            RentalEventsTopic = "rental.events.v1"
        }));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var mockExecutor = new Mock<IRentalOperationExecutor>();

        var workflowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:RentalEventsTopic"] = "rental.events.v1"
        }).Build();

        var paymentWorkflow = new PaymentWorkflowService(
            orders, inboxes, outboxes, receipts, lockManager,
            mockExecutor.Object, db, cmdProducer, workflowConfig,
            NullLogger<PaymentWorkflowService>.Instance, eventProducer, timeProvider, notifier);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager, paymentWorkflow,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            new Mock<IInventoryAllocationResultHandler>().Object,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null,
            null,
            notifier);

        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // Renter receives PAYMENT_REFUND_REQUIRED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.PaymentRefundRequired, renterNote.Notification.ChangeType);

        // Suppliers receive NOTHING
        Assert.Empty(notifier.SupplierNotifications);
    }
}
