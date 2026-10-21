using System.Text.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Application.Services.Settlement;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Moq;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class VersionPersistenceRegressionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateTestOrder(Guid orderId, Guid renterId, Guid supplierId, params Guid[] itemIds)
    {
        var items = itemIds.Select((itemId, index) =>
        {
            var assignments = new List<InitialRentalUnitAssignmentDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            };

            return new InitialRentalItemDto(
                itemId,
                Guid.NewGuid(),
                supplierId,
                1,
                new DateOnly(2026, 10, 20),
                new DateOnly(2026, 10, 25),
                $"Device {index + 1}",
                "Description",
                "Kho HCM",
                100_000m,
                500_000m,
                $"group-{itemId:N}",
                assignments,
                TestListingSnapshots.Default);
        }).ToList();

        return RentalOrder.Create(
            orderId,
            $"ORD-{orderId.ToString("N")[..8]}",
            renterId,
            "VND",
            "Pickup",
            "Customer A",
            "0901234567",
            "123 Street",
            null,
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create",
            FixedNow,
            items).Value;
    }

    [Fact]
    public async Task RefundReplay_SameMessageId_ReturnsImmediately_NewMessageId_PersistsInbox_WithoutIncrementingVersionOrAddingAudit()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        item.ApplySupplierReject(supplierId, FixedNow, "Reject reason", null, "op-rej-1");
        RentalOrderStatusPolicy.Apply(order, FixedNow, "op-rej-1");

        string refundOpKey = "refund:op:001";
        var refundItems = new List<RefundItemRequest> { new(itemId, item.RentalAmount, item.DepositAmount) };
        var refundReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            orderId,
            refundOpKey,
            "RequestRefund",
            "hash-1",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusPending,
            targetItemId: itemId,
            payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(orderId, "INV-001", "PAY-001", refundItems, "Reject reason", refundOpKey)));
        order.AddOperationReceipt(refundReceipt);

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        Guid messageId1 = Guid.NewGuid();
        var payload = new RefundCompletedPayload(
            orderId, "INV-001", "PAY-001", "REF-001", refundOpKey,
            item.RentalAmount + item.DepositAmount, "VND",
            [new RefundCompletedItemPayload(itemId, item.RentalAmount, item.DepositAmount)],
            FixedNow);

        var envelope1 = new KafkaMessageEnvelope<RefundCompletedPayload>(
            messageId1, "RefundCompleted", 1, "payment-service", orderId.ToString("D"), FixedNow, "corr-1", null, payload);

        // 1. First execution: newly applied
        await using (var processDb = CreateDbContext(dbName))
        {
            var consumer = new RefundCompletedConsumer(
                new RentalOrderRepository(processDb),
                new InboxRepository(processDb),
                new FakeOrderLockManager(),
                processDb,
                NullLogger<RefundCompletedConsumer>.Instance,
                TimeProvider.System);

            var res1 = await consumer.ProcessRefundCompletedAsync(envelope1);
            Assert.True(res1.IsSuccess);
        }

        long versionAfterFirst;
        DateTimeOffset updatedAfterFirst;
        int transitionsAfterFirst;

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            versionAfterFirst = o.Version;
            updatedAfterFirst = o.UpdatedAt;
            transitionsAfterFirst = o.StateTransitions.Count;

            Assert.Equal(2, versionAfterFirst); // 1 -> 2
            Assert.Contains(o.StateTransitions, t => t.Action == "RefundCompleted");

            var inbox1 = await verifyDb.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId1);
            Assert.NotNull(inbox1);
            Assert.Equal(RentalInboxMessage.StatusProcessed, inbox1.Status);
        }

        // 2. Replay with SAME MessageId: returns immediately without creating duplicate data
        await using (var replayDb1 = CreateDbContext(dbName))
        {
            var consumer = new RefundCompletedConsumer(
                new RentalOrderRepository(replayDb1),
                new InboxRepository(replayDb1),
                new FakeOrderLockManager(),
                replayDb1,
                NullLogger<RefundCompletedConsumer>.Instance,
                TimeProvider.System);

            var resReplay1 = await consumer.ProcessRefundCompletedAsync(envelope1);
            Assert.True(resReplay1.IsSuccess);
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionAfterFirst, o.Version);
            Assert.Equal(updatedAfterFirst, o.UpdatedAt);
            Assert.Equal(transitionsAfterFirst, o.StateTransitions.Count);
        }

        // 3. Replay with NEW MessageId for the same refund:
        // Must save the new inbox message as Processed, but NOT increment Version and NOT add new audit transition!
        Guid messageId2 = Guid.NewGuid();
        var envelope2 = new KafkaMessageEnvelope<RefundCompletedPayload>(
            messageId2, "RefundCompleted", 1, "payment-service", orderId.ToString("D"), FixedNow.AddMinutes(5), "corr-2", null, payload);

        await using (var replayDb2 = CreateDbContext(dbName))
        {
            var consumer = new RefundCompletedConsumer(
                new RentalOrderRepository(replayDb2),
                new InboxRepository(replayDb2),
                new FakeOrderLockManager(),
                replayDb2,
                NullLogger<RefundCompletedConsumer>.Instance,
                TimeProvider.System);

            var resReplay2 = await consumer.ProcessRefundCompletedAsync(envelope2);
            Assert.True(resReplay2.IsSuccess);
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionAfterFirst, o.Version);
            Assert.Equal(updatedAfterFirst, o.UpdatedAt);
            Assert.Equal(transitionsAfterFirst, o.StateTransitions.Count);

            // New inbox was saved and processed
            var inbox2 = await verifyDb.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId2);
            Assert.NotNull(inbox2);
            Assert.Equal(RentalInboxMessage.StatusProcessed, inbox2.Status);
        }
    }

    [Fact]
    public async Task SettlementScheduling_And_ReturnRejection_PersistsReceipt_WithoutIncrementingVersion()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        order.MarkAsPaid("INV-SETTLE-001", "PAY-SETTLE-001", FixedNow, FixedNow);

        var item = order.Items.First();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
        var handover = new HandoverRecord(Guid.NewGuid(), item.Id, FixedNow.AddDays(-2), null, item.SupplierId, FixedNow.AddDays(-2), FixedNow.AddDays(-2));
        item.SetHandoverRecord(handover);
        foreach (var u in item.UnitAssignments)
        {
            u.RecordHandover(handover.Id, UnitCondition.Normal, null, []);
            handover.AddAssignment(u);
        }
        var unit = item.UnitAssignments.First();
        var retResult = item.RecordAssetReturns(supplierId, FixedNow, null, [new ReturnUnitData(unit.Id, UnitCondition.Normal, null, [])], FixedNow, supplierId);
        Assert.True(retResult.IsSuccess);

        // Finalize a deduction
        item.TryFinalizeZeroDeduction(FixedNow, "ded-zero");
        // Confirm closure
        var closeResult = item.ConfirmReturnedUnitClosure(unit.Id, FixedNow, null, "INV-REC-1", FixedNow);
        Assert.True(closeResult.IsSuccess);

        Assert.True(item.IsReadyForSettlement);

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        long versionBeforeSettlementSchedule;
        DateTimeOffset updatedBeforeSettlementSchedule;

        await using (var db = CreateDbContext(dbName))
        {
            var o = await db.RentalOrders.FirstAsync(x => x.Id == orderId);
            versionBeforeSettlementSchedule = o.Version;
            updatedBeforeSettlementSchedule = o.UpdatedAt;

            var deductionService = new DeductionService(
                new RentalOrderRepository(db),
                new FakeOrderLockManager(),
                db,
                new StubMediaClient(),
                TimeProvider.System);

            // 1. Schedule settlement: creates pending SettleRental receipt
            var schedResult = await deductionService.ScheduleSettlementAsync(itemId);
            Assert.True(schedResult.IsSuccess, $"{schedResult.Error.Code}: {schedResult.Error.Message}");
        }

        // Verify receipt persisted but Version and UpdatedAt did NOT increment
        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionBeforeSettlementSchedule, o.Version);
            Assert.Equal(updatedBeforeSettlementSchedule, o.UpdatedAt);

            var settleReceipt = o.OperationReceipts.FirstOrDefault(r => r.Action == RentalItem.SettleRentalAction);
            Assert.NotNull(settleReceipt);
            Assert.Equal(RentalOperationReceipt.StatusPending, settleReceipt.Status);
        }

        // 2. InventoryReturnResultHandler.RejectAsync: persists failed receipt without incrementing Version
        Guid returnRecId;
        string returnOpId;
        string allocGroupId;

        await using (var setupDb = CreateDbContext(dbName))
        {
            var o = await setupDb.RentalOrders
                .Include(x => x.Items).ThenInclude(i => i.ReturnRecords)
                .Include(x => x.Items).ThenInclude(i => i.UnitAssignments)
                .Include(x => x.OperationReceipts)
                .FirstAsync(x => x.Id == orderId);

            var it = o.Items.First();
            var retRec = it.ReturnRecords.First();
            returnRecId = retRec.Id;
            allocGroupId = it.AllocationGroupId;
            returnOpId = $"return:{returnRecId:N}:apply";

            ReturnService.ScheduleKafkaInventoryReturn(o, it, retRec, FixedNow);
            await setupDb.SaveChangesAsync();
        }

        long versionBeforeReturnReject;
        DateTimeOffset updatedBeforeReturnReject;

        await using (var rejectDb = CreateDbContext(dbName))
        {
            var o = await rejectDb.RentalOrders.FirstAsync(x => x.Id == orderId);
            versionBeforeReturnReject = o.Version;
            updatedBeforeReturnReject = o.UpdatedAt;

            var returnHandler = new InventoryReturnResultHandler(
                new RentalOrderRepository(rejectDb),
                TimeProvider.System,
                new FakeRentalEventProducer());

            string returnOpKey = RentalOperationExecutor.ComputeOperationKey(RentalItem.ApplyReturnedUnitsAction, returnOpId);
            var rejectPayload = new ReturnedUnitsRejectedPayload(orderId, itemId, allocGroupId, returnRecId, returnOpKey, "INVENTORY_ERROR", "Unit damaged in transit");
            var rejectEnvelope = new KafkaMessageEnvelope<ReturnedUnitsRejectedPayload>(
                Guid.NewGuid(), KafkaContractSpec.EventTypes.ReturnedUnitsRejected, 1,
                KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), FixedNow, returnOpId, null, rejectPayload);
            await using var tx = await rejectDb.BeginTransactionAsync();
            var rejResult = await returnHandler.RejectStagedAsync(JsonSerializer.Serialize(rejectEnvelope, JsonOptions));
            Assert.True(rejResult.IsSuccess, $"{rejResult.Error.Code}: {rejResult.Error.Message}");
            await rejectDb.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionBeforeReturnReject, o.Version);
            Assert.Equal(updatedBeforeReturnReject, o.UpdatedAt);

            var returnReceipt = o.OperationReceipts.First(r => r.OperationId == returnOpId);
            Assert.Equal(RentalOperationReceipt.StatusFailed, returnReceipt.Status);
            Assert.Equal("INVENTORY_ERROR", returnReceipt.ErrorCode);
        }
    }

    [Fact]
    public async Task AdminReplay_IncrementsVersionOnNewReplay_ReplayWithSameKeyDoesNotIncrement()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        order.MarkAsPaid("INV-ADM-001", "PAY-ADM-001", FixedNow, FixedNow);

        string failedOpId = "failed-op-1";
        var failedReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            orderId,
            failedOpId,
            "SupplierAccept",
            "hash-adm",
            isSuccess: false,
            errorCode: "TIMEOUT",
            errorMessage: "Inventory call timed out",
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusFailed,
            targetItemId: itemId,
            payloadJson: "{\"validJson\":true}");
        order.AddOperationReceipt(failedReceipt);

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        long versionBeforeReplay;
        DateTimeOffset updatedBeforeReplay;
        int transitionsBeforeReplay;

        await using (var db = CreateDbContext(dbName))
        {
            var o = await db.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            versionBeforeReplay = o.Version;
            updatedBeforeReplay = o.UpdatedAt;
            transitionsBeforeReplay = o.StateTransitions.Count;

            var config = new ConfigurationBuilder().Build();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(db),
                new OperationReceiptRepository(db),
                new OutboxRepository(db),
                new FakeOrderLockManager(),
                Mock.Of<IInventoryRentalClient>(),
                Mock.Of<IPaymentRentalClient>(),
                db,
                config,
                NullLogger<RentalOperationExecutor>.Instance,
                TimeProvider.System);

            // 1. New Admin Replay -> must increment Version and append transition
            var replayRes = await executor.ExecuteAdminReplayAsync(
                orderId, failedOpId, Guid.NewGuid(), "Admin needs to retry accept", "idemp-key-1");
            Assert.True(replayRes.IsSuccess);
        }

        long versionAfterFirstReplay;
        DateTimeOffset updatedAfterFirstReplay;
        int transitionsAfterFirstReplay;

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            versionAfterFirstReplay = o.Version;
            updatedAfterFirstReplay = o.UpdatedAt;
            transitionsAfterFirstReplay = o.StateTransitions.Count;

            Assert.Equal(versionBeforeReplay + 1, versionAfterFirstReplay);
            Assert.Equal(transitionsBeforeReplay + 1, transitionsAfterFirstReplay);
            Assert.Contains(o.StateTransitions, t => t.Action == "AdminReplayRequested");
        }

        // 2. Replay with SAME idempotency key -> must NOT increment Version or add transition
        await using (var db = CreateDbContext(dbName))
        {
            var config = new ConfigurationBuilder().Build();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(db),
                new OperationReceiptRepository(db),
                new OutboxRepository(db),
                new FakeOrderLockManager(),
                Mock.Of<IInventoryRentalClient>(),
                Mock.Of<IPaymentRentalClient>(),
                db,
                config,
                NullLogger<RentalOperationExecutor>.Instance,
                TimeProvider.System);
            var replayResSameKey = await executor.ExecuteAdminReplayAsync(
                orderId, failedOpId, Guid.NewGuid(), "Admin needs to retry accept", "idemp-key-1");
            Assert.True(replayResSameKey.IsSuccess);
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.StateTransitions).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionAfterFirstReplay, o.Version);
            Assert.Equal(updatedAfterFirstReplay, o.UpdatedAt);
            Assert.Equal(transitionsAfterFirstReplay, o.StateTransitions.Count);
        }
    }
    [Fact]
    public async Task PaymentExpiry_IncrementsVersionOnce_AndPersistsOutbox_RetryDoesNotChangeVersion_InvoiceAlreadyPaidRejects()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))!.SetValue(order, "INV-EXP-001");

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        var mockPayment = new Mock<IPaymentRentalClient>();
        mockPayment.Setup(p => p.CancelInvoiceAsync(It.IsAny<CancelInvoiceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CancelInvoiceRequest req, CancellationToken _) =>
                Result.Success(new CancelInvoiceResponse("INV-EXP-001", true, FixedNow, false)));

        var mockInventory = new Mock<IInventoryRentalClient>();

        long versionAfterExpiry;
        DateTimeOffset updatedAfterExpiry;
        var expiryTimeProvider = new FakeTimeProvider(FixedNow.AddHours(1));

        // 1. Initial Payment Expiry execution
        await using (var db = CreateDbContext(dbName))
        {
            var config = new ConfigurationBuilder().Build();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(db),
                new OperationReceiptRepository(db),
                new OutboxRepository(db),
                new FakeOrderLockManager(),
                mockInventory.Object,
                mockPayment.Object,
                db,
                config,
                NullLogger<RentalOperationExecutor>.Instance,
                expiryTimeProvider);

            var result = await executor.ExecutePaymentExpiryAsync(orderId, "op-expiry-1");
            Assert.True(result.IsSuccess);
        }

        // Verify Order & Items are Expired, Version incremented once, Outbox saved
        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.Items).FirstAsync(x => x.Id == orderId);
            Assert.Equal(RentalOrderStatus.Expired, o.Status);
            Assert.All(o.Items, i => Assert.Equal(RentalItemStatus.Expired, i.Status));
            Assert.Equal(2, o.Version); // 1 -> 2
            versionAfterExpiry = o.Version;
            updatedAfterExpiry = o.UpdatedAt;

            var outbox = await verifyDb.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderExpired);
            Assert.NotNull(outbox);
        }

        // 2. Retry Payment Expiry: should not change Version or UpdatedAt
        await using (var db = CreateDbContext(dbName))
        {
            var config = new ConfigurationBuilder().Build();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(db),
                new OperationReceiptRepository(db),
                new OutboxRepository(db),
                new FakeOrderLockManager(),
                mockInventory.Object,
                mockPayment.Object,
                db,
                config,
                NullLogger<RentalOperationExecutor>.Instance,
                expiryTimeProvider);

            var retryResult = await executor.ExecutePaymentExpiryAsync(orderId, "op-expiry-1");
            Assert.True(retryResult.IsSuccess);
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionAfterExpiry, o.Version);
            Assert.Equal(updatedAfterExpiry, o.UpdatedAt);
        }

        // 3. Invoice already paid conflict: does not expire order or increment Version
        string paidDbName = Guid.NewGuid().ToString("N");
        Guid paidOrderId = Guid.NewGuid();
        var paidOrder = CreateTestOrder(paidOrderId, renterId, supplierId, Guid.NewGuid());
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))!.SetValue(paidOrder, "INV-PAID-001");

        await using (var seedDb = CreateDbContext(paidDbName))
        {
            seedDb.RentalOrders.Add(paidOrder);
            await seedDb.SaveChangesAsync();
        }

        var mockPaidPayment = new Mock<IPaymentRentalClient>();
        mockPaidPayment.Setup(p => p.CancelInvoiceAsync(It.IsAny<CancelInvoiceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<CancelInvoiceResponse>(
                Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Invoice was already paid")));

        await using (var db = CreateDbContext(paidDbName))
        {
            var config = new ConfigurationBuilder().Build();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(db),
                new OperationReceiptRepository(db),
                new OutboxRepository(db),
                new FakeOrderLockManager(),
                mockInventory.Object,
                mockPaidPayment.Object,
                db,
                config,
                NullLogger<RentalOperationExecutor>.Instance,
                expiryTimeProvider);

            var res = await executor.ExecutePaymentExpiryAsync(paidOrderId, "op-exp-paid");
            Assert.True(res.IsSuccess);
        }

        await using (var verifyDb = CreateDbContext(paidDbName))
        {
            var o = await verifyDb.RentalOrders.FirstAsync(x => x.Id == paidOrderId);
            Assert.Equal(RentalOrderStatus.PendingPayment, o.Status);
            Assert.Equal(1, o.Version); // Unchanged
        }
    }

    [Fact]
    public async Task ReleaseSuccess_IncrementsVersion_ReplayWithNewMessageIdPreservesVersionAndClosureTimestamp_ReleaseFailureOnlyUpdatesReceipt()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        order.MarkAsPaid("INV-REL-001", "PAY-REL-001", FixedNow, FixedNow);

        var item = order.Items.First();
        item.ApplySupplierTimeout(FixedNow, "op-timeout-1");
        RentalOrderStatusPolicy.Apply(order, FixedNow, "op-timeout-1");

        string releaseOpKey = "release:op:001";
        var releaseReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), orderId, releaseOpKey, "ReleaseAllocation", "fp-rel", FixedNow, itemId, "{}");
        order.AddOperationReceipt(releaseReceipt);

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        DateTimeOffset closureTime = FixedNow.AddMinutes(10);
        var testTimeProvider = new FakeTimeProvider(closureTime);

        // 1. Release allocation success
        await using (var db = CreateDbContext(dbName))
        {
            var handler = new InventoryAllocationResultHandler(
                new RentalOrderRepository(db),
                db,
                new FakeOrderLockManager(),
                testTimeProvider);

            var inbox = new RentalInboxMessage(
                Guid.NewGuid(), Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), null, null,
                JsonSerializer.Serialize(new { payload = new { commandType = "ReleaseAllocation", operationKey = releaseOpKey, rentalOrderId = orderId, rentalItemId = itemId } }),
                closureTime);

            var currentOrder = await db.RentalOrders.Include(x => x.Items).ThenInclude(i => i.UnitAssignments).Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            var res = await handler.HandleAllocationEventAsync(orderId, inbox, currentOrder, CancellationToken.None);
            Assert.True(res.IsSuccess);
            await db.SaveChangesAsync();
        }

        long versionAfterRelease;
        DateTimeOffset updatedAfterRelease;

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.Items).ThenInclude(i => i.UnitAssignments).Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            var it = o.Items.First();
            var unit = it.UnitAssignments.First();

            Assert.Equal(RentalUnitAssignmentStatus.Released, unit.Status);
            Assert.Equal(closureTime, unit.AllocationClosedAt);
            Assert.Equal(2, o.Version); // Version incremented on release
            versionAfterRelease = o.Version;
            updatedAfterRelease = o.UpdatedAt;

            var rcpt = o.OperationReceipts.First(r => r.OperationId == releaseOpKey);
            Assert.Equal(RentalOperationReceipt.StatusSucceeded, rcpt.Status);
        }

        // 2. Replay with NEW message ID for the SAME release at a later time
        DateTimeOffset replayTime = closureTime.AddMinutes(15);
        var replayTimeProvider = new FakeTimeProvider(replayTime);

        await using (var db = CreateDbContext(dbName))
        {
            var handler = new InventoryAllocationResultHandler(
                new RentalOrderRepository(db),
                db,
                new FakeOrderLockManager(),
                replayTimeProvider);

            var replayInbox = new RentalInboxMessage(
                Guid.NewGuid(), Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), null, null,
                JsonSerializer.Serialize(new { payload = new { commandType = "ReleaseAllocation", operationKey = releaseOpKey, rentalItemId = itemId } }),
                replayTime);

            var currentOrder = await db.RentalOrders.Include(x => x.Items).ThenInclude(i => i.UnitAssignments).Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            var res = await handler.HandleAllocationEventAsync(orderId, replayInbox, currentOrder, CancellationToken.None);
            Assert.True(res.IsSuccess);
            await db.SaveChangesAsync();
        }

        // Verify closure timestamp is PRESERVED, Version and UpdatedAt are UNCHANGED
        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.Items).ThenInclude(i => i.UnitAssignments).FirstAsync(x => x.Id == orderId);
            var it = o.Items.First();
            var unit = it.UnitAssignments.First();

            Assert.Equal(closureTime, unit.AllocationClosedAt); // Preserved!
            Assert.Equal(versionAfterRelease, o.Version);       // Not incremented!
            Assert.Equal(updatedAfterRelease, o.UpdatedAt);     // Not changed!
        }

        // 3. Release failure: only updates receipt, does NOT increment Version
        string failOpKey = "release:fail:001";
        await using (var setupDb = CreateDbContext(dbName))
        {
            var o = await setupDb.RentalOrders.Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            o.AddOperationReceipt(RentalOperationReceipt.CreatePending(
                Guid.NewGuid(), orderId, failOpKey, "ReleaseAllocation", "fp-fail", replayTime, itemId, "{}"));
            await setupDb.SaveChangesAsync();
        }

        await using (var db = CreateDbContext(dbName))
        {
            var handler = new InventoryAllocationResultHandler(
                new RentalOrderRepository(db),
                db,
                new FakeOrderLockManager(),
                replayTimeProvider);

            var failInbox = new RentalInboxMessage(
                Guid.NewGuid(), Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected,
                KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), null, null,
                JsonSerializer.Serialize(new { payload = new { commandType = "ReleaseAllocation", operationKey = failOpKey, rentalItemId = itemId, errorCode = "INVENTORY_ERROR", errorMessage = "Network timeout" } }),
                replayTime);

            var currentOrder = await db.RentalOrders.Include(x => x.Items).ThenInclude(i => i.UnitAssignments).Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            var res = await handler.HandleAllocationEventAsync(orderId, failInbox, currentOrder, CancellationToken.None);
            Assert.True(res.IsSuccess);
            await db.SaveChangesAsync();
        }

        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.OperationReceipts).FirstAsync(x => x.Id == orderId);
            Assert.Equal(versionAfterRelease, o.Version); // Version did NOT increment on failure
            var rcpt = o.OperationReceipts.First(r => r.OperationId == failOpKey);
            Assert.Equal(RentalOperationReceipt.StatusFailed, rcpt.Status);
        }
    }

    [Fact]
    public async Task RefundFailure_DiscardChanges_NoPartialPersistenceOrInboxProcessed()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = CreateTestOrder(orderId, renterId, supplierId, itemId);
        order.MarkAsPaid("INV-FAIL-001", "PAY-FAIL-001", FixedNow, FixedNow);

        var item = order.Items.First();
        item.ApplySupplierReject(supplierId, FixedNow, "Reject reason", null, "op-rej-fail");
        RentalOrderStatusPolicy.Apply(order, FixedNow, "op-rej-fail");

        string refundOpKey = "refund:fail:001";
        var refundItems = new List<RefundItemRequest> { new(itemId, item.RentalAmount, item.DepositAmount) };
        var refundReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            orderId,
            refundOpKey,
            "RequestRefund",
            "hash-fail",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusPending,
            targetItemId: itemId,
            payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(orderId, "INV-FAIL-001", "PAY-FAIL-001", refundItems, "Reject reason", refundOpKey)));
        order.AddOperationReceipt(refundReceipt);

        await using (var seedDb = CreateDbContext(dbName))
        {
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        Guid messageId = Guid.NewGuid();
        var payload = new RefundCompletedPayload(
            orderId, "INV-FAIL-001", "PAY-FAIL-001", "REF-FAIL-001", refundOpKey,
            item.RentalAmount + item.DepositAmount, "VND",
            [new RefundCompletedItemPayload(itemId, item.RentalAmount, item.DepositAmount)],
            FixedNow);

        var envelope = new KafkaMessageEnvelope<RefundCompletedPayload>(
            messageId, "RefundCompleted", 1, "payment-service", orderId.ToString("D"), FixedNow, "corr-fail", null, payload);

        // Use a mock UnitOfWork whose SaveChangesAsync throws DbUpdateConcurrencyException
        await using (var db = CreateDbContext(dbName))
        {
            var mockUow = new Mock<IUnitOfWork>();
            var mockTx = new Mock<IUnitOfWorkTransaction>();
            mockUow.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockTx.Object);
            mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DbUpdateConcurrencyException("Simulated concurrency failure"));

            var consumer = new RefundCompletedConsumer(
                new RentalOrderRepository(db),
                new InboxRepository(db),
                new FakeOrderLockManager(),
                mockUow.Object,
                NullLogger<RefundCompletedConsumer>.Instance,
                TimeProvider.System);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => consumer.ProcessRefundCompletedAsync(envelope));
            mockTx.Verify(t => t.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
            mockUow.Verify(u => u.DiscardChanges(), Times.Once);
        }

        // Verify in DB that nothing was persisted (item not refunded, version unchanged, inbox not processed)
        await using (var verifyDb = CreateDbContext(dbName))
        {
            var o = await verifyDb.RentalOrders.Include(x => x.Items).FirstAsync(x => x.Id == orderId);
            Assert.Equal(1, o.Version);
            var it = o.Items.First();
            Assert.Null(it.RefundRef);
            Assert.Equal(RentalItemStatus.Rejected, it.Status);

            var inbox = await verifyDb.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId);
            Assert.Null(inbox);
        }
    }


    private sealed class StubMediaClient : IRentalMediaClient
    {
        public Task<Result<MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new MediaVerifyDto(mediaId, Guid.NewGuid().ToString("D"), "Ready", "photo.jpg", "image/jpeg", 1024, FixedNow)));

        public Task<Result<MediaMetadataDto>> GetMetadataAsync(Guid mediaId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready", FixedNow)));

        public Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CreateMediaAccessRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new MediaAccessDto(mediaId, "https://storage.example.com/photo.jpg", FixedNow.AddHours(1))));
    }

    private sealed class FakeOrderLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        public Task<IAsyncDisposable> AcquireRenterLockAsync(Guid renterUserId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
