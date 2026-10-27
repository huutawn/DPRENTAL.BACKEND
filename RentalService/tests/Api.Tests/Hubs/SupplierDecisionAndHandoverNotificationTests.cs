using Dprental.RentalService.Application.IServices.Payments;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Application.Services.SupplierDecision;
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

public sealed class SupplierDecisionAndHandoverNotificationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 14, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateMultiSupplierPaidOrder(Guid orderId, Guid renterId, Guid supplierA, Guid supplierB, out RentalItem itemA, out RentalItem itemB)
    {
        var unitA = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemA = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Thiết bị A", "Desc", "Kho A", 1_000_000m, 5_000_000m,
            "grp-A", [unitA],
            TestListingSnapshots.Default);

        var unitB = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemB = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Thiết bị B", "Desc", "Kho B", 2_000_000m, 8_000_000m,
            "grp-B", [unitB],
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId, "DH-2026-MULTI-01", renterId, "VND", "Pickup",
            "Nguyễn Văn An", "0901234567", "123 Đường A", null,
            "v1.0", "terms-doc", "terms-hash", "op-create", FixedNow,
            [initialItemA, initialItemB]).Value;

        order.MarkAsPaid("INV-MULTI-01", "PAY-MULTI-01", FixedNow, FixedNow);

        itemA = order.Items.Single(i => i.SupplierId == supplierA);
        itemB = order.Items.Single(i => i.SupplierId == supplierB);
        return order;
    }

    private static RentalOrder CreateMultiSupplierPendingOrder(Guid orderId, Guid renterId, Guid supplierA, Guid supplierB)
    {
        var unitA = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemA = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Thiết bị A", "Desc", "Kho A", 1_000_000m, 5_000_000m,
            "grp-A", [unitA],
            TestListingSnapshots.Default);

        var unitB = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var initialItemB = new InitialRentalItemDto(
            Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
            new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
            "Thiết bị B", "Desc", "Kho B", 2_000_000m, 8_000_000m,
            "grp-B", [unitB],
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId, "DH-2026-MULTI-02", renterId, "VND", "Pickup",
            "Nguyễn Văn An", "0901234567", "123 Đường A", null,
            "v1.0", "terms-doc", "terms-hash", "op-create", FixedNow,
            [initialItemA, initialItemB]).Value;
    }

    [Fact]
    public async Task SupplierRejectSubOrder_EmitsSupplierRejected_ToRenterAndOnlyRejectingSupplier()
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
        var kafkaOptions = Options.Create(new KafkaOptions());

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out _, out _);
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

        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;

        // Supplier A rejects their suborder
        var rejectResult = await executor.ExecuteSubOrderRejectAsync(
            orderId, subOrderAId, supplierA, "Hết máy cho thuê", null, "reject-idemp-1");

        Assert.True(rejectResult.IsSuccess);

        // Verify Renter received SUPPLIER_REJECTED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.SupplierRejected, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // Verify Supplier A received SUPPLIER_REJECTED for their suborder
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.SupplierRejected, supplierNote.Notification.ChangeType);

        // Supplier B MUST NOT receive anything (Supplier A's reject doesn't leak to Supplier B)
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task InventoryConfirmAllocation_Succeeded_EmitsSupplierConfirmed_ToRenterAndOnlyImpactedSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out RentalItem itemA, out _);

        // Set up pending receipt for ConfirmAllocation on Item A
        string opKey = "confirm-op-A";
        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "SupplierAccept", "fp-A", FixedNow, itemA.Id, "{}");
        receipt.SetInventoryOperationKey(opKey);
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            RentalEventsTopic = "rental.events.v1"
        }));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);
        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = opKey,
            commandType = KafkaContractSpec.CommandTypes.ConfirmAllocation
        };

        var envelope = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            1,
            "inventory-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-alloc",
            null,
            payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-alloc");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        // Process allocation event -> item transitions to AwaitingHandover and commits
        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // 1. Renter received SUPPLIER_CONFIRMED (No physical info!)
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.SupplierConfirmed, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A received SUPPLIER_CONFIRMED for their suborder
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.SupplierConfirmed, supplierNote.Notification.ChangeType);

        // 3. Supplier B received NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task InventoryConfirmAllocation_Rejected_DoesNotEmitSupplierConfirmed()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, Guid.NewGuid(), out RentalItem itemA, out _);

        string opKey = "confirm-op-reject";
        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "SupplierAccept", "fp-A", FixedNow, itemA.Id, "{}");
        receipt.SetInventoryOperationKey(opKey);
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);
        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = opKey,
            commandType = KafkaContractSpec.CommandTypes.ConfirmAllocation,
            errorCode = "INVENTORY_ALLOCATION_CONFLICT",
            errorMessage = "Asset not available"
        };

        var envelope = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, // REJECTED!
            1,
            "inventory-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-alloc",
            null,
            payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-alloc");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);

        // Neither renter nor supplier should receive confirmation on rejection
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task InventoryMarkAssetsHandedOver_Succeeded_EmitsHandoverConfirmed_AfterEvidencePersistenceAndCommit()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out RentalItem itemA, out _);

        // Move Item A to AwaitingHandover
        itemA.ApplySupplierAccept(supplierA, FixedNow, "conf-1", "conf-1");

        // Set up pending handover receipt with evidence snapshot
        string opKey = "handover-op-A";
        var snapshot = new HandoverEvidenceSnapshot(
            supplierA, FixedNow, FixedNow, "Đã giao máy",
            [new HandoverUnitSnapshot(itemA.UnitAssignments.First().Id, UnitCondition.Normal, "Tốt", [Guid.NewGuid()])]);

        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "MarkAssetsHandedOver", "fp-H", FixedNow, itemA.Id, snapshot.ToJson());
        receipt.SetInventoryOperationKey(opKey);
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);
        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = opKey,
            commandType = KafkaContractSpec.CommandTypes.MarkAssetsHandedOver
        };

        var envelope = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            1,
            "inventory-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-handover",
            null,
            payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-handover");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        // Process handover confirmation -> commits HandoverRecord and transitions to InRental
        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // 1. Renter received HANDOVER_CONFIRMED with no physical data
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.HandoverConfirmed, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A received HANDOVER_CONFIRMED for their suborder
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.HandoverConfirmed, supplierNote.Notification.ChangeType);

        // 3. Supplier B received NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task AcceptSubOrder_CommandAccepted_DoesNotEmitSupplierConfirmed()
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
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, Guid.NewGuid(), out RentalItem itemA, out _);

        // Hold must be Succeeded to accept
        string holdOpKey = $"ExtendHold:{orderId}:{itemA.Id}";
        var holdReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, holdOpKey, "ExtendHold", "fp-hold", FixedNow, itemA.Id, "{}");
        holdReceipt.MarkSucceeded(FixedNow, "{}", "{}");
        order.AddOperationReceipt(holdReceipt);

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

        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;

        // Accept command enqueued -> command accepted is NOT completion
        var acceptResult = await executor.ExecuteSubOrderAcceptAsync(
            orderId, subOrderAId, supplierA, "accept-idemp-1");

        Assert.True(acceptResult.IsSuccess);

        // Neither renter nor supplier should receive SUPPLIER_CONFIRMED yet
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task InventoryMarkAssetsHandedOver_DuplicateReply_DoesNotEmitDuplicateNotification()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, Guid.NewGuid(), out RentalItem itemA, out _);

        // Receipt is ALREADY Succeeded
        string opKey = "handover-op-dup";
        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "MarkAssetsHandedOver", "fp-H", FixedNow, itemA.Id, "{}");
        receipt.SetInventoryOperationKey(opKey);
        receipt.MarkSucceeded(FixedNow, "{}");
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);

        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = opKey,
            commandType = KafkaContractSpec.CommandTypes.MarkAssetsHandedOver
        };

        var envelope = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            1,
            "inventory-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-handover",
            null,
            payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-dup");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // Duplicate reply must NOT emit notifications
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task ReleaseAllocation_Success_EmitsAllocationChanged_ToRenterAndAffectedSupplierOnly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out _, out _);
        order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);

        var itemA = order.Items.First(i => i.SupplierId == supplierA);
        string opKey = "rel-op-1";
        var relReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "ReleaseAllocation", "fp-rel",
            FixedNow, itemA.Id, "{}");
        order.AddOperationReceipt(relReceipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);

        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = opKey,
            commandType = KafkaContractSpec.CommandTypes.ReleaseAllocation
        };

        var envelope = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            1,
            "inventory-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-rel",
            null,
            payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-rel");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // Renter received ALLOCATION_CHANGED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(OrderChangeTypes.AllocationChanged, renterNote.Notification.ChangeType);

        // Supplier A received ALLOCATION_CHANGED
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(OrderChangeTypes.AllocationChanged, supplierNote.Notification.ChangeType);

        // Supplier B received NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task RecoveryHold_AllSucceed_EmitsPaymentConfirmed_ToAllSuppliers()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPendingOrder(orderId, renterId, supplierA, supplierB);
        order.AttachInvoice("INV-REC", FixedNow);
        order.RecordPaymentReceived("INV-REC", "PAY-REC", FixedNow, FixedNow);

        var itemA = order.Items.First(i => i.SupplierId == supplierA);
        var itemB = order.Items.First(i => i.SupplierId == supplierB);

        string recOpKeyA = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{orderId}:{itemA.Id}");
        string recOpKeyB = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{orderId}:{itemB.Id}");

        var receiptA = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, recOpKeyA, "RecoveryHold", "fp-rec-a",
            FixedNow, itemA.Id, "{}");
        var receiptB = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, recOpKeyB, "RecoveryHold", "fp-rec-b",
            FixedNow, itemB.Id, "{}");
        order.AddOperationReceipt(receiptA);
        order.AddOperationReceipt(receiptB);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        // 1. First reply for item A succeeds -> does NOT emit PaymentConfirmed yet because item B is pending
        var payloadA = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = recOpKeyA,
            commandType = KafkaContractSpec.CommandTypes.CreateHold
        };
        var envA = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, 1,
            "inventory-service", orderId.ToString("D"), FixedNow, "corr-a", null, payloadA);
        var rawA = JsonSerializer.Serialize(envA, JsonOptions);
        var msgA = new RentalInboxMessage(Guid.NewGuid(), envA.MessageId, envA.MessageType, envA.Producer, envA.AggregateId, envA.CorrelationId, null, rawA, FixedNow, "fp-a");
        await inboxes.AddAsync(msgA);
        await db.SaveChangesAsync();

        var resA = await dispatcher.ProcessInboxMessageAsync(msgA.Id);
        Assert.True(resA.IsSuccess);
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);

        // 2. Second reply for item B succeeds -> now order is Paid! Emits PAYMENT_CONFIRMED to renter and both suppliers
        var payloadB = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemB.Id,
            operationKey = recOpKeyB,
            commandType = KafkaContractSpec.CommandTypes.CreateHold
        };
        var envB = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, 1,
            "inventory-service", orderId.ToString("D"), FixedNow, "corr-b", null, payloadB);
        var rawB = JsonSerializer.Serialize(envB, JsonOptions);
        var msgB = new RentalInboxMessage(Guid.NewGuid(), envB.MessageId, envB.MessageType, envB.Producer, envB.AggregateId, envB.CorrelationId, null, rawB, FixedNow, "fp-b");
        await inboxes.AddAsync(msgB);
        await db.SaveChangesAsync();

        var resB = await dispatcher.ProcessInboxMessageAsync(msgB.Id);
        Assert.True(resB.IsSuccess, resB.IsFailure ? $"{resB.Error.Code}: {resB.Error.Message}" : "");

        Assert.Single(notifier.RenterNotifications);
        Assert.Equal(OrderChangeTypes.PaymentConfirmed, notifier.RenterNotifications.Single().Notification.ChangeType);

        Assert.Equal(2, notifier.SupplierNotifications.Count);
        Assert.All(notifier.SupplierNotifications, s => Assert.Equal(OrderChangeTypes.PaymentConfirmed, s.Notification.ChangeType));
    }

    [Fact]
    public async Task RecoveryHold_Failure_EmitsPaymentRefundRequired_ToRenterOnly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPendingOrder(orderId, renterId, supplierA, supplierB);
        order.AttachInvoice("INV-REC-FAIL", FixedNow);
        order.RecordPaymentReceived("INV-REC-FAIL", "PAY-REC-FAIL", FixedNow, FixedNow);

        var itemA = order.Items.First(i => i.SupplierId == supplierA);
        string recOpKeyA = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{orderId}:{itemA.Id}");

        var receiptA = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, recOpKeyA, "RecoveryHold", "fp-rec-a",
            FixedNow, itemA.Id, "{}");
        order.AddOperationReceipt(receiptA);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider,
            rentalEvents: eventProducer);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null, null, notifier);

        var payloadA = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemA.Id,
            operationKey = recOpKeyA,
            commandType = KafkaContractSpec.CommandTypes.CreateHold,
            errorCode = "INSUFFICIENT_AVAILABILITY",
            errorMessage = "No available units"
        };
        var envA = new KafkaMessageEnvelope<object>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, 1,
            "inventory-service", orderId.ToString("D"), FixedNow, "corr-a-fail", null, payloadA);
        var rawA = JsonSerializer.Serialize(envA, JsonOptions);
        var msgA = new RentalInboxMessage(Guid.NewGuid(), envA.MessageId, envA.MessageType, envA.Producer, envA.AggregateId, envA.CorrelationId, null, rawA, FixedNow, "fp-a-fail");
        await inboxes.AddAsync(msgA);
        await db.SaveChangesAsync();

        var resA = await dispatcher.ProcessInboxMessageAsync(msgA.Id);
        Assert.True(resA.IsSuccess);

        // Renter receives PAYMENT_REFUND_REQUIRED
        Assert.Single(notifier.RenterNotifications);
        Assert.Equal(OrderChangeTypes.PaymentRefundRequired, notifier.RenterNotifications.Single().Notification.ChangeType);

        // Previously visible order compensation notifies suppliers so they drop old data
        Assert.Equal(2, notifier.SupplierNotifications.Count);
        Assert.Contains(notifier.SupplierNotifications, n => n.SupplierId == supplierA && n.Notification.ChangeType == OrderChangeTypes.PaymentRefundRequired);
        Assert.Contains(notifier.SupplierNotifications, n => n.SupplierId == supplierB && n.Notification.ChangeType == OrderChangeTypes.PaymentRefundRequired);
    }
}
