using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.IServices.Payments;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Application.Services.Settlement;
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

public sealed class ReturnRefundAndSettlementNotificationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 16, 0, 0, TimeSpan.Zero);
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
            orderId, "DH-2026-MULTI-RET", renterId, "VND", "Pickup",
            "Nguyễn Văn An", "0901234567", "123 Đường A", null,
            "v1.0", "terms-doc", "terms-hash", "op-create", FixedNow,
            [initialItemA, initialItemB]).Value;

        order.MarkAsPaid("INV-PAID-01", "PAY-PAID-01", FixedNow, FixedNow);

        itemA = order.Items.Single(i => i.SupplierId == supplierA);
        itemB = order.Items.Single(i => i.SupplierId == supplierB);
        return order;
    }

    [Fact]
    public async Task ReturnedUnitsApplied_EmitsReturnRecorded_ToRenterAndOnlyImpactedSupplier()
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

        OrderTestData.HandOver(itemA, FixedNow.AddDays(-1));
        // Supplier records return
        var unitData = new ReturnUnitData(itemA.UnitAssignments.First().Id, UnitCondition.Normal, "OK", []);
        var returnRec = itemA.RecordAssetReturns(supplierA, FixedNow, null, [unitData], FixedNow, supplierA).Value;

        // Pending ApplyReturnedUnits command receipt
        string opKey = $"return:{returnRec.Id:N}:apply";
        var cmd = new ApplyReturnedUnitsCommandPayload(
            orderId, itemA.Id, itemA.AllocationGroupId, returnRec.Id, FixedNow, opKey,
            [new ApplyReturnedUnitPayload(itemA.UnitAssignments.First().AllocationId, "NORMAL")]);

        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, RentalItem.ApplyReturnedUnitsAction, "fp-ret", FixedNow, itemA.Id, JsonSerializer.Serialize(cmd));
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);

        var returnResultHandler = new InventoryReturnResultHandler(orders, timeProvider, eventProducer);
        var allocHandler = new InventoryAllocationResultHandler(orders, db, lockManager, timeProvider, rentalEvents: eventProducer);

        // Inventory confirms ReturnedUnitsApplied
        var payload = new ReturnedUnitsAppliedPayload(
            orderId, itemA.Id, itemA.AllocationGroupId, returnRec.Id, opKey, "RECEIPT-RET-1", FixedNow,
            [new AppliedReturnedUnitPayload(itemA.UnitAssignments.First().AllocationId, itemA.UnitAssignments.First().AssetId, "RELEASED", FixedNow, null)]);

        var envelope = new KafkaMessageEnvelope<ReturnedUnitsAppliedPayload>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.ReturnedUnitsApplied, 1,
            KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), FixedNow, "corr-ret", null, payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-ret");

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler, kafkaOptions, null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider, null, returnResultHandler, notifier);

        var res = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res.IsSuccess);

        // 1. Renter receives RETURN_RECORDED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.ReturnRecorded, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A receives RETURN_RECORDED for their suborder
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.ReturnRecorded, supplierNote.Notification.ChangeType);

        // 3. Supplier B receives NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task DeductionProposal_Submitted_EmitsSettlementChanged_ToRenterAndOnlyImpactedSupplier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out RentalItem itemA, out _);

        OrderTestData.HandOver(itemA, FixedNow.AddDays(-1));
        var unitData = new ReturnUnitData(itemA.UnitAssignments.First().Id, UnitCondition.Damaged, "Trầy xước nặng", [Guid.NewGuid()]);
        itemA.RecordAssetReturns(supplierA, FixedNow, null, [unitData], FixedNow, supplierA);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        Guid mediaId = Guid.NewGuid();
        var mockMedia = new Mock<IRentalMediaClient>();
        mockMedia.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, supplierA.ToString("D"), "Ready")));
        mockMedia.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready")));

        var deductionService = new DeductionService(orders, lockManager, db, mockMedia.Object, timeProvider, notifier);
        var caller = OrderTestData.User(supplierA);

        var request = new SubmitDeductionRequest(
            DeductionAmount: 1_000_000m,
            DamageDescription: "Trầy xước nặng thân máy",
            EvidenceMediaIds: [mediaId],
            AffectedAssetIds: [itemA.UnitAssignments.First().AssetId]);

        // Submit deduction proposal -> commits and emits SETTLEMENT_CHANGED
        var result = await deductionService.SubmitAsync(itemA.Id, caller, request, "deduction-submit-1");
        Assert.True(result.IsSuccess);

        // 1. Renter receives SETTLEMENT_CHANGED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.SettlementChanged, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // 2. Supplier A receives SETTLEMENT_CHANGED
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(order.Version, supplierNote.Notification.OrderVersion);
        Assert.Equal(OrderChangeTypes.SettlementChanged, supplierNote.Notification.ChangeType);

        // 3. Supplier B receives NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);
    }

    [Fact]
    public async Task DeductionResponse_RenterAccept_EmitsSettlementChanged()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, Guid.NewGuid(), out RentalItem itemA, out _);

        OrderTestData.HandOver(itemA, FixedNow.AddDays(-1));
        var unitData = new ReturnUnitData(itemA.UnitAssignments.First().Id, UnitCondition.Damaged, "Trầy", [Guid.NewGuid()]);
        itemA.RecordAssetReturns(supplierA, FixedNow, null, [unitData], FixedNow, supplierA);
        itemA.SubmitDeduction(supplierA, 500_000m, "Trầy sơn", [Guid.NewGuid()], [itemA.UnitAssignments.First().AssetId], FixedNow);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var mockMedia = new Mock<IRentalMediaClient>();
        var responseService = new DeductionResponseService(
            orders, lockManager, db, mockMedia.Object, timeProvider,
            new ConfigurationBuilder().Build(), NullLogger<DeductionResponseService>.Instance, notifier);

        var caller = OrderTestData.User(renterId);

        // Renter accepts deduction
        var res = await responseService.RespondAsync(
            itemA.Id, caller, new RespondDeductionRequest(RenterDeductionDecision.Accept, null, null), "resp-accept-1");
        Assert.True(res.IsSuccess);

        // 1. Renter receives SETTLEMENT_CHANGED
        Assert.Single(notifier.RenterNotifications);
        Assert.Equal(OrderChangeTypes.SettlementChanged, notifier.RenterNotifications.Single().Notification.ChangeType);

        // 2. Supplier A receives SETTLEMENT_CHANGED
        Assert.Single(notifier.SupplierNotifications);
        Assert.Equal(supplierA, notifier.SupplierNotifications.Single().SupplierId);
        Assert.Equal(OrderChangeTypes.SettlementChanged, notifier.SupplierNotifications.Single().Notification.ChangeType);
    }

    [Fact]
    public async Task ReceiptOnlyBookkeeping_ScheduleSettlement_DoesNotEmitNotification()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, Guid.NewGuid(), out RentalItem itemA, out _);

        // Normal return with zero deduction finalized
        itemA.ApplySupplierAccept(supplierA, FixedNow, "conf-1", "conf-1");
        var handoverRec = new HandoverRecord(Guid.NewGuid(), itemA.Id, FixedNow, null, supplierA, FixedNow, FixedNow);
        itemA.SetHandoverRecord(handoverRec);
        foreach (var u in itemA.UnitAssignments) u.RecordHandover(handoverRec.Id, UnitCondition.Normal, null, []);
        var unitData = new ReturnUnitData(itemA.UnitAssignments.First().Id, UnitCondition.Normal, "OK", []);
        itemA.RecordAssetReturns(supplierA, FixedNow, null, [unitData], FixedNow, supplierA);
        itemA.ConfirmReturnedUnitClosure(itemA.UnitAssignments.First().Id, FixedNow, null, "REF-1", FixedNow);
        itemA.TryFinalizeZeroDeduction(FixedNow, "zero-op");

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var deductionService = new DeductionService(orders, lockManager, db, new Mock<IRentalMediaClient>().Object, timeProvider, notifier);

        // ScheduleSettlement creates an internal pending receipt for background workers
        var res = await deductionService.ScheduleSettlementAsync(itemA.Id);
        Assert.True(res.IsSuccess);

        // Receipt-only bookkeeping MUST NOT emit realtime notifications
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task RefundCompleted_EmitsRefundChanged_ToRenterOnly_AndNotOnReplay()
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

        // Put item into RefundRequested state
        itemA.ApplySupplierReject(supplierA, FixedNow, "Hết máy", null, "rej-1");
        string opKey = "refund-op-1";
        var refundReq = new RequestRefundRequest(
            orderId, "INV-PAID-01", "PAY-PAID-01",
            [new RefundItemRequest(itemA.Id, itemA.RentalAmount, itemA.DepositAmount)],
            "Hết máy", opKey, RentalRefundKinds.CancellationRefund);
        string payloadJson = JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(refundReq) });
        var refundReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, "RequestRefund", "fp-ref", FixedNow, itemA.Id, payloadJson);
        order.AddOperationReceipt(refundReceipt);
        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var refundConsumer = new RefundCompletedConsumer(orders, inboxes, lockManager, db, NullLogger<RefundCompletedConsumer>.Instance, timeProvider);

        var payload = new RefundCompletedPayload(
            orderId, "INV-PAID-01", "PAY-PAID-01", "REF-COMP-1", opKey,
            itemA.RentalAmount + itemA.DepositAmount,
            "VND", [new RefundCompletedItemPayload(itemA.Id, itemA.RentalAmount, itemA.DepositAmount)], FixedNow);

        var envelope = new KafkaMessageEnvelope<RefundCompletedPayload>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.RefundCompleted, 1,
            KafkaContractSpec.Producers.PaymentService, orderId.ToString("D"), FixedNow, "corr-ref", null, payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-ref");
        inboxMsg.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(rawJson), orderId.ToString("D"));

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            refundConsumer,
            new KafkaOutboxWriter(outboxes), db,
            new Mock<IInventoryAllocationResultHandler>().Object,
            kafkaOptions, null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider, null, null, notifier);

        // 1. Process RefundCompleted first time -> emits REFUND_CHANGED to renter only
        var res1 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res1.IsSuccess);

        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.RefundChanged, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);
        Assert.Empty(notifier.SupplierNotifications); // Supplier does not have refund visibility

        // 2. Replay same message -> no duplicate notification
        notifier.Reset();
        var res2 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res2.IsSuccess);
        Assert.Empty(notifier.RenterNotifications);
    }

    [Fact]
    public async Task RentalSettled_EmitsSettlementChanged_ToRenterAndSettledSupplier_AndNotOnReplay()
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

        // Put item in closure confirmed state
        itemA.ApplySupplierAccept(supplierA, FixedNow, "conf-1", "conf-1");
        OrderTestData.HandOver(itemA, FixedNow.AddDays(-1));
        var unitData = new ReturnUnitData(itemA.UnitAssignments.First().Id, UnitCondition.Normal, "OK", []);
        itemA.RecordAssetReturns(supplierA, FixedNow, null, [unitData], FixedNow, supplierA);
        itemA.ConfirmReturnedUnitClosure(itemA.UnitAssignments.First().Id, FixedNow, null, "REF-1", FixedNow);
        itemA.TryFinalizeZeroDeduction(FixedNow, "zero-op");

        string opKey = "settle-op-1";
        var settleReq = new SettleRentalRequest(
            orderId, itemA.Id, "INV-PAID-01", "PAY-PAID-01",
            supplierA, itemA.DepositAmount, 0m, itemA.DepositAmount, "VND", "DEC-1", opKey);
        var receipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, opKey, RentalItem.SettleRentalAction, "fp-settle", FixedNow, itemA.Id,
            JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(settleReq) }));
        order.AddOperationReceipt(receipt);

        await orders.CreateAsync(order);
        await db.SaveChangesAsync();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions()));
        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
        var settledConsumer = new RentalSettledConsumer(eventProducer, timeProvider);

        var payload = new RentalSettledPayload(
            orderId, itemA.Id, "INV-PAID-01", "PAY-PAID-01", "SETTLE-REF-1", opKey,
            0m, itemA.DepositAmount, "VND", null, FixedNow);

        var envelope = new KafkaMessageEnvelope<RentalSettledPayload>(
            Guid.NewGuid(), KafkaContractSpec.EventTypes.RentalSettled, 1,
            KafkaContractSpec.Producers.PaymentService, orderId.ToString("D"), FixedNow, "corr-settle", null, payload);

        string rawJson = JsonSerializer.Serialize(envelope, JsonOptions);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, envelope.MessageType,
            envelope.Producer, envelope.AggregateId, envelope.CorrelationId, null,
            rawJson, FixedNow, "fp-inbox-settle");
        inboxMsg.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(rawJson), orderId.ToString("D"));

        await inboxes.AddAsync(inboxMsg);
        await db.SaveChangesAsync();

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager,
            new Mock<IPaymentWorkflowService>().Object,
            new Mock<IRefundCompletedConsumer>().Object,
            new KafkaOutboxWriter(outboxes), db,
            new Mock<IInventoryAllocationResultHandler>().Object,
            kafkaOptions, null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider, settledConsumer, null, notifier);

        // 1. Process RentalSettled first time -> emits SETTLEMENT_CHANGED to renter and supplierA
        var res1 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res1.IsSuccess);

        // Renter received SETTLEMENT_CHANGED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.SettlementChanged, renterNote.Notification.ChangeType);
        Assert.Equal(order.Version, renterNote.Notification.Version);

        // Supplier A received SETTLEMENT_CHANGED
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(OrderChangeTypes.SettlementChanged, supplierNote.Notification.ChangeType);

        // Supplier B received NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);

        // 2. Replay same message -> no duplicate notification
        notifier.Reset();
        var res2 = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(res2.IsSuccess);
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }

    [Fact]
    public async Task Return_Submitted_EmitsReturnSubmitted_ToRenterAndOwningSupplierOnly_EvenWhenInventoryPending()
    {
        string dbName = Guid.NewGuid().ToString("N");
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();
        var timeProvider = new FixedTimeProvider(FixedNow);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierA = Guid.NewGuid();
        Guid supplierB = Guid.NewGuid();
        var order = CreateMultiSupplierPaidOrder(orderId, renterId, supplierA, supplierB, out RentalItem itemA, out _);

        OrderTestData.HandOver(itemA, FixedNow.AddDays(-1));
        await using (var seedCtx = CreateDbContext(dbName))
        {
            var seedOrders = new RentalOrderRepository(seedCtx);
            await seedOrders.CreateAsync(order);
            await seedCtx.SaveChangesAsync();
        }

        await using var db = CreateDbContext(dbName);
        var orders = new RentalOrderRepository(db);
        var eventProducer = new FakeRentalEventProducer();
        var mockCmdProducer = new Mock<IInventoryCommandProducer>();
        mockCmdProducer.Setup(p => p.EnqueueApplyReturnedUnitsAsync(It.IsAny<ApplyReturnedUnitsCommandPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ApplyReturnedUnits", "topic", "key")));

        var mediaClient = Mock.Of<IRentalMediaClient>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:Enabled"] = "true"
        }).Build();

        var returnService = new ReturnService(
            orders, lockManager, db, mediaClient, timeProvider, eventProducer, config, mockCmdProducer.Object, notifier);
        var caller = OrderTestData.User(supplierA);
        var unit = itemA.UnitAssignments.First();
        var request = new RecordReturnRequest(
            FixedNow, "Trả máy tại kho",
            [new ReturnUnitRequest(unit.Id, UnitCondition.Normal, "Tốt", [])]);

        long versionBefore = order.Version;

        // 1. Submit return -> committed, emitted RETURN_SUBMITTED before any inventory reply
        var res = await returnService.RecordReturnAsync(itemA.Id, caller, request, "idemp-return-1");
        Assert.True(res.IsSuccess);

        // Renter received RETURN_SUBMITTED
        Assert.Single(notifier.RenterNotifications);
        var renterNote = notifier.RenterNotifications.Single();
        Assert.Equal(renterId, renterNote.RenterId);
        Assert.Equal(orderId, renterNote.Notification.OrderId);
        Assert.Equal(OrderChangeTypes.ReturnSubmitted, renterNote.Notification.ChangeType);
        Assert.Equal(versionBefore + 1, renterNote.Notification.Version);

        // Supplier A received RETURN_SUBMITTED
        Assert.Single(notifier.SupplierNotifications);
        var supplierNote = notifier.SupplierNotifications.Single();
        Assert.Equal(supplierA, supplierNote.SupplierId);
        Assert.Equal(orderId, supplierNote.Notification.OrderId);
        Guid subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
        Assert.Equal(subOrderAId, supplierNote.Notification.SubOrderId);
        Assert.Equal(OrderChangeTypes.ReturnSubmitted, supplierNote.Notification.ChangeType);
        Assert.Equal(versionBefore + 1, supplierNote.Notification.OrderVersion);

        // Supplier B received NOTHING
        Assert.DoesNotContain(notifier.SupplierNotifications, n => n.SupplierId == supplierB);

        // Replay HTTP with same idempotency key -> no duplicate notification
        notifier.Reset();
        var res2 = await returnService.RecordReturnAsync(itemA.Id, caller, request, "idemp-return-1");
        Assert.True(res2.IsSuccess);
        Assert.Empty(notifier.RenterNotifications);
        Assert.Empty(notifier.SupplierNotifications);
    }
}
