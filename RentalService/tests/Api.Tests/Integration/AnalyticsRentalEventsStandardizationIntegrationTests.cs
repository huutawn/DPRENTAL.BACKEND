using System.Linq;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.Security;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class AnalyticsRentalEventsStandardizationIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;
        public void SetUtcNow(DateTimeOffset time) => _utcNow = time;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static readonly HashSet<string> AllowedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Envelope fields
        "messageId", "messageType", "version", "producer", "aggregateId", "occurredAt", "correlationId", "causationId", "payload",
        // Business common
        "orderVersion", "rentalOrderId", "orderId", "orderCode", "renterId", "userId", "supplierId", "currency",
        // Items & financial
        "items", "rentalItemId", "equipmentListingId", "equipmentTypeId", "equipmentTypeName", "brandId", "brandName", "model",
        "quantity", "expectedStartDate", "expectedEndDate", "rentalAmount", "depositAmount", "totalRentalAmount", "totalDepositAmount", "totalPayableAmount",
        // Status & references
        "paymentDeadline", "createdAt", "decisionAt", "supplierConfirmationDeadline", "paidAmount", "paidAt", "invoiceRef", "paymentRef",
        "expiredAt", "cancelledAt", "completedAt", "actualHandoverAt", "actualReturnAt", "submittedAt", "returnsCompletedAt", "overdueAt",
        "handoverRecordId", "returnRecordId", "assignmentIds", "inventoryConfirmationRef", "reason", "optionalNote", "role", "actorId", "refundAmount",
        "totalRefundAmount", "requestedAt"
    };

    [Fact]
    public async Task CompleteLifecycle_EmitsStandardizedEvents_WithCorrectVersions_AndExplicitMapping()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var initCtx = new RentalServiceDbContext(options);
            await initCtx.Database.MigrateAsync();

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();

            var clock = new MutableTimeProvider(FixedNow);
            var kafkaOptions = Options.Create(new KafkaOptions
            {
                Enabled = true,
                RentalEventsTopic = "rental.events.v1"
            });

            // 1. CHECKOUT CREATION
            var line = OrderTestData.Line(supplierId, "Sony FX3 Camera", "Camera", "Sony", 1, 500_000m, 2_000_000m);
            var order = OrderTestData.Order(renterId, "RNT-ANALYTICS-001", FixedNow, line);
            initCtx.RentalOrders.Add(order);
            await initCtx.SaveChangesAsync();
            var outboxRepo = new OutboxRepository(initCtx);
            var producer = new RentalEventProducer(new KafkaOutboxWriter(outboxRepo), kafkaOptions, clock);

            await using (var tx1 = await initCtx.Database.BeginTransactionAsync())
            {
                var createdRes = await producer.EnqueueRentalOrderCreatedAsync(
                    new RentalOrderCreatedPayload(
                        order.Id,
                        order.OrderCode,
                        order.RenterId,
                        order.Currency,
                        order.TotalRentalAmount,
                        order.TotalDepositAmount,
                        order.TotalPayableAmount,
                        order.PaymentDeadline,
                        order.CreatedAt,
                        order.Items.Select(i => new RentalOrderCreatedItemPayload(
                            i.Id,
                            i.EquipmentListingId,
                            i.SupplierId,
                            i.Quantity,
                            i.ExpectedStartDate,
                            i.ExpectedEndDate,
                            i.RentalAmount,
                            i.DepositAmount,
                            i.EquipmentTypeId,
                            i.EquipmentTypeNameSnapshot,
                            i.BrandId,
                            i.BrandNameSnapshot,
                            i.ModelSnapshot)).ToArray(),
                        order.Version),
                    correlationId: order.Id.ToString("D"));
                Assert.True(createdRes.IsSuccess);
                await initCtx.SaveChangesAsync();
                await tx1.CommitAsync();
            }

            // Attach invoice after Created event is emitted
            order.AttachInvoice("INV-001", FixedNow);
            await initCtx.SaveChangesAsync();
            // Verify Created event in DB
            await using var verifyCtx1 = new RentalServiceDbContext(options);
            var createdOutbox = await verifyCtx1.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderCreated);
            Assert.Equal("rental.events.v1", createdOutbox.Topic);
            Assert.Equal(order.Id.ToString("D"), createdOutbox.PartitionKey);

            using var createdDoc = JsonDocument.Parse(createdOutbox.Payload);
            var createdPayload = createdDoc.RootElement.GetProperty("payload");
            Assert.Equal(1, createdPayload.GetProperty("orderVersion").GetInt64());
            var createdItems = createdPayload.GetProperty("items");
            Assert.Equal(1, createdItems.GetArrayLength());
            var firstItem = createdItems[0];
            Assert.Equal("Sony FX3 Camera", firstItem.GetProperty("model").GetString());
            Assert.Equal("Sony", firstItem.GetProperty("brandName").GetString());
            Assert.Equal("Camera", firstItem.GetProperty("equipmentTypeName").GetString());
            AssertAllPropertiesAllowed(createdDoc.RootElement);

            // 2. PAYMENT RECEIVED -> PAID
            DateTimeOffset paidTime = FixedNow.AddMinutes(10);
            clock.SetUtcNow(paidTime);
            var orderRepo = new RentalOrderRepository(verifyCtx1);
            var loadedOrder = await orderRepo.GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(loadedOrder);

            var lockManager = new PostgresOrderLockManager(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = conn }).Build(), NullLogger<PostgresOrderLockManager>.Instance);
            var payProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx1)), kafkaOptions, clock);
            var executor = new RentalOperationExecutor(
                orderRepo,
                new OperationReceiptRepository(verifyCtx1),
                new OutboxRepository(verifyCtx1),
                lockManager,
                new Mock<IInventoryRentalClient>().Object,
                new Mock<IPaymentRentalClient>().Object,
                new InventoryCommandProducer(new OutboxRepository(verifyCtx1), kafkaOptions, clock),
                verifyCtx1,
                new ConfigurationBuilder().Build(),
                NullLogger<RentalOperationExecutor>.Instance,
                timeProvider: clock,
                rentalEvents: payProducer);

            var payService = new PaymentWorkflowService(
                orderRepo,
                new InboxRepository(verifyCtx1),
                new OutboxRepository(verifyCtx1),
                new OperationReceiptRepository(verifyCtx1),
                lockManager,
                executor,
                verifyCtx1,
                new InventoryCommandProducer(new OutboxRepository(verifyCtx1), kafkaOptions, clock),
                new ConfigurationBuilder().Build(),
                NullLogger<PaymentWorkflowService>.Instance,
                payProducer,
                clock);

            var invoicePayload = new InvoicePaidPayload(order.Id, loadedOrder.RenterId, "INV-001", "PAY-001", loadedOrder.TotalPayableAmount, "VND", paidTime);
            var invoiceEnvelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
                Guid.NewGuid(), KafkaContractSpec.EventTypes.InvoicePaid, 1, "payment-service", order.Id.ToString("D"), paidTime, "corr-pay", null, invoicePayload);

            var payRes = await payService.ProcessInvoicePaidAsync(invoiceEnvelope);
            Assert.True(payRes.IsSuccess, payRes.IsFailure ? $"{payRes.Error.Code}: {payRes.Error.Message}" : "");

            // Verify Paid events
            await using var verifyCtx2 = new RentalServiceDbContext(options);
            var reloadedAfterPay = await verifyCtx2.RentalOrders.SingleAsync(o => o.Id == order.Id);
            Assert.Equal(3, reloadedAfterPay.Version);

            var orderPaidOutbox = await verifyCtx2.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderPaid);
            using var orderPaidDoc = JsonDocument.Parse(orderPaidOutbox.Payload);
            Assert.Equal(3, orderPaidDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            Assert.Equal(order.Id.ToString("D"), orderPaidOutbox.PartitionKey);
            AssertAllPropertiesAllowed(orderPaidDoc.RootElement);

            var itemPaidOutbox = await verifyCtx2.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemPaid);
            using var itemPaidDoc = JsonDocument.Parse(itemPaidOutbox.Payload);
            Assert.Equal(3, itemPaidDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            Assert.Equal(order.Id.ToString("D"), itemPaidOutbox.PartitionKey);
            AssertAllPropertiesAllowed(itemPaidDoc.RootElement);

            // 3. CONFIRM ALLOCATION -> RentalItemConfirmed
            DateTimeOffset confTime = FixedNow.AddHours(2);
            clock.SetUtcNow(confTime);
            var confirmOrder = await new RentalOrderRepository(verifyCtx2).GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(confirmOrder);
            var itemToConfirm = confirmOrder.Items.First();

            itemToConfirm.ApplySupplierAccept(supplierId, confTime, "CONF-INV-01", "op-conf");
            RentalOrderStatusPolicy.Apply(confirmOrder, confTime, "op-conf");
            confirmOrder.RecordReadModelUpdate(confTime);
            Assert.Equal(4, confirmOrder.Version);

            await using (var txConf = await verifyCtx2.Database.BeginTransactionAsync())
            {
                var confirmProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx2)), kafkaOptions, clock);
                var confEnqRes = await confirmProducer.EnqueueRentalItemConfirmedAsync(
                    new RentalItemConfirmedPayload(order.Id, itemToConfirm.Id, supplierId, confTime, "CONF-INV-01", confirmOrder.Version),
                    correlationId: "op-conf");
                Assert.True(confEnqRes.IsSuccess);
                await verifyCtx2.SaveChangesAsync();
                await txConf.CommitAsync();
            }

            await using var verifyCtx3 = new RentalServiceDbContext(options);
            var confOutbox = await verifyCtx3.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemConfirmed);
            using var confDoc = JsonDocument.Parse(confOutbox.Payload);
            Assert.Equal(4, confDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(confDoc.RootElement);

            // 4. HANDOVER -> RentalItemHandoverCompleted
            DateTimeOffset handoverTime = FixedNow.AddDays(2);
            clock.SetUtcNow(handoverTime);
            var handoverOrder = await new RentalOrderRepository(verifyCtx3).GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(handoverOrder);
            var handedItem = handoverOrder.Items.First();
            Guid handoverRecId = Guid.NewGuid();
            var hRec = new HandoverRecord(handoverRecId, handedItem.Id, handoverTime, null, supplierId, handoverTime, handoverTime);
            handedItem.SetHandoverRecord(hRec);
            verifyCtx3.HandoverRecords.Add(hRec);
            handedItem.ApplyHandover(handoverTime, "op-handover");
            handedItem.UnitAssignments.First().RecordHandover(handoverRecId, UnitCondition.Normal, null, []);
            handoverOrder.RecordReadModelUpdate(handoverTime);
            Assert.Equal(5, handoverOrder.Version);

            await using (var txHand = await verifyCtx3.Database.BeginTransactionAsync())
            {
                var handoverProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx3)), kafkaOptions, clock);
                var handoverEnqRes = await handoverProducer.EnqueueRentalItemHandoverCompletedAsync(
                    new RentalItemHandoverCompletedPayload(
                        order.Id, handedItem.Id, handoverOrder.RenterId, supplierId, handedItem.EquipmentListingId, handedItem.Quantity, handoverRecId, handoverTime, handoverTime, handoverOrder.Version));
                Assert.True(handoverEnqRes.IsSuccess);
                await verifyCtx3.SaveChangesAsync();
                await txHand.CommitAsync();
            }
            await using var verifyCtx4 = new RentalServiceDbContext(options);
            var handoverOutbox = await verifyCtx4.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemHandoverCompleted);
            using var handoverDoc = JsonDocument.Parse(handoverOutbox.Payload);
            Assert.Equal(5, handoverDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(handoverDoc.RootElement);

            // 5. RETURN RECORDED -> RentalUnitsReturned
            DateTimeOffset returnTime = FixedNow.AddDays(4);
            clock.SetUtcNow(returnTime);
            var returnOrder = await new RentalOrderRepository(verifyCtx4).GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(returnOrder);
            var returningItem = returnOrder.Items.First();
            var unitData = new ReturnUnitData(returningItem.UnitAssignments.First().Id, UnitCondition.Normal, null, []);
            var returnRecordRes = returningItem.RecordAssetReturns(supplierId, returnTime, null, [unitData], returnTime, supplierId);
            Assert.True(returnRecordRes.IsSuccess);
            verifyCtx4.ReturnRecords.Add(returnRecordRes.Value);
            RentalOrderStatusPolicy.Apply(returnOrder, returnTime, "op-return");
            returnOrder.RecordReadModelUpdate(returnTime);
            Assert.Equal(6, returnOrder.Version);
            await using (var txRet = await verifyCtx4.Database.BeginTransactionAsync())
            {
                var returnProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx4)), kafkaOptions, clock);
                var returnUnitsRes = await returnProducer.EnqueueRentalUnitsReturnedAsync(
                    new Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload(order.Id, returningItem.Id, returnRecordRes.Value.Id, returnTime, returnTime, [returningItem.UnitAssignments.First().Id], returnOrder.Version),
                    correlationId: "op-return");
                Assert.True(returnUnitsRes.IsSuccess);
                await verifyCtx4.SaveChangesAsync();
                await txRet.CommitAsync();
            }
            await using var verifyCtx5 = new RentalServiceDbContext(options);
            var unitsReturnedOutbox = await verifyCtx5.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalUnitsReturned);
            using var unitsRetDoc = JsonDocument.Parse(unitsReturnedOutbox.Payload);
            Assert.Equal(6, unitsRetDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(unitsRetDoc.RootElement);

            // 6. INVENTORY CLOSURE CONFIRMED -> RentalItemReturned
            DateTimeOffset closureTime = FixedNow.AddDays(4).AddHours(1);
            clock.SetUtcNow(closureTime);
            var closureOrder = await new RentalOrderRepository(verifyCtx5).GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(closureOrder);
            var closedItem = closureOrder.Items.First();
            closedItem.ConfirmReturnedUnitClosure(closedItem.UnitAssignments.First().Id, closureTime, null, "INVENTORY-CLOSED-01", closureTime);
            RentalOrderStatusPolicy.Apply(closureOrder, closureTime, "op-closure");
            closureOrder.RecordReadModelUpdate(closureTime);
            Assert.Equal(7, closureOrder.Version);
            await using (var txClose = await verifyCtx5.Database.BeginTransactionAsync())
            {
                var closureProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx5)), kafkaOptions, clock);
                var itemRetRes = await closureProducer.EnqueueRentalItemReturnedAsync(
                    new RentalItemReturnedPayload(order.Id, closedItem.Id, closureOrder.RenterId, supplierId, closedItem.EquipmentListingId, closedItem.Quantity, closureTime, closureOrder.Version));
                Assert.True(itemRetRes.IsSuccess);
                await verifyCtx5.SaveChangesAsync();
                await txClose.CommitAsync();
            }
            await using var verifyCtx6 = new RentalServiceDbContext(options);
            var itemReturnedOutbox = await verifyCtx6.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemReturned);
            using var itemRetDoc = JsonDocument.Parse(itemReturnedOutbox.Payload);
            Assert.Equal(7, itemRetDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(itemRetDoc.RootElement);

            // 7. SETTLEMENT COMPLETED -> RentalItemCompleted & RentalOrderCompleted
            DateTimeOffset settleTime = FixedNow.AddDays(5);
            clock.SetUtcNow(settleTime);
            var settleOrder = await new RentalOrderRepository(verifyCtx6).GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(settleOrder);
            var settledItem = settleOrder.Items.First();
            settledItem.SetStatus(RentalItemStatus.Completed);
            RentalOrderStatusPolicy.Apply(settleOrder, settleTime, "op-settle");
            settleOrder.RecordReadModelUpdate(settleTime);
            Assert.Equal(8, settleOrder.Version);
            Assert.Equal(RentalOrderStatus.Completed, settleOrder.Status);
            await using (var txSettle = await verifyCtx6.Database.BeginTransactionAsync())
            {
                var settleProducer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(verifyCtx6)), kafkaOptions, clock);
                var itemCompRes = await settleProducer.EnqueueRentalItemCompletedAsync(
                    new RentalItemCompletedPayload(settleOrder.RenterId, settledItem.EquipmentListingId, settledItem.EquipmentTypeId, order.Id, settledItem.Id, settleOrder.Version));
                Assert.True(itemCompRes.IsSuccess);

                var orderCompRes = await settleProducer.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(order.Id, settleOrder.RenterId, settleTime, settleOrder.Version));
                Assert.True(orderCompRes.IsSuccess);
                await verifyCtx6.SaveChangesAsync();
                await txSettle.CommitAsync();
            }
            await using var verifyCtx7 = new RentalServiceDbContext(options);
            var itemCompletedOutbox = await verifyCtx7.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemCompleted);
            using var itemCompDoc = JsonDocument.Parse(itemCompletedOutbox.Payload);
            Assert.Equal(8, itemCompDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(itemCompDoc.RootElement);

            var orderCompletedOutbox = await verifyCtx7.RentalOutboxMessages.SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderCompleted);
            using var orderCompDoc = JsonDocument.Parse(orderCompletedOutbox.Payload);
            Assert.Equal(8, orderCompDoc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(orderCompDoc.RootElement);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task SubOrderReject_EmitsStandardizedReason_And_FinalBatchVersion()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var initCtx = new RentalServiceDbContext(options);
            await initCtx.Database.MigrateAsync();

            Guid supplierId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();

            var clock = new MutableTimeProvider(OrderTestData.Now.AddMinutes(10));
            var line1 = OrderTestData.Line(supplierId, "Máy đầm Mikasa 1", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m);
            var line2 = OrderTestData.Line(supplierId, "Máy đầm Mikasa 2", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m);
            var order = OrderTestData.Paid(OrderTestData.Order(renterId, "DH-SUB-01", OrderTestData.Now, line1, line2), OrderTestData.Now.AddMinutes(5));
            initCtx.RentalOrders.Add(order);
            await initCtx.SaveChangesAsync();

            var lockManager = new PostgresOrderLockManager(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = conn }).Build(), NullLogger<PostgresOrderLockManager>.Instance);
            var kafkaOptions = Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = "rental.events.v1" });
            var producer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(initCtx)), kafkaOptions, clock);

            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(initCtx),
                new OperationReceiptRepository(initCtx),
                new OutboxRepository(initCtx),
                lockManager,
                new Mock<IInventoryRentalClient>().Object,
                new Mock<IPaymentRentalClient>().Object,
                new InventoryCommandProducer(new OutboxRepository(initCtx), kafkaOptions, clock),
                initCtx,
                new ConfigurationBuilder().Build(),
                NullLogger<RentalOperationExecutor>.Instance,
                timeProvider: clock,
                rentalEvents: producer);

            Guid subOrderId = order.Items.First().RentalSubOrderId;

            var rejectResult = await executor.ExecuteSubOrderRejectAsync(
                order.Id,
                subOrderId,
                supplierId,
                reason: "Equipment damaged in storage",
                note: "Sensitive note for customer",
                idempotencyKey: "reject-sub-001",
                cancellationToken: CancellationToken.None);

            Assert.True(rejectResult.IsSuccess, rejectResult.IsFailure ? $"{rejectResult.Error.Code}: {rejectResult.Error.Message}" : "");

            // Re-read DB
            await using var verifyCtx = new RentalServiceDbContext(options);
            var reloadedOrder = await verifyCtx.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            Assert.Equal(2, reloadedOrder.Version); // Incremented once for the batch

            var rejectMessages = await verifyCtx.RentalOutboxMessages
                .Where(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemRejected)
                .ToListAsync();

            Assert.Equal(2, rejectMessages.Count);
            foreach (var msg in rejectMessages)
            {
                using var doc = JsonDocument.Parse(msg.Payload);
                var payload = doc.RootElement.GetProperty("payload");
                Assert.Equal(2, payload.GetProperty("orderVersion").GetInt64());
                Assert.Equal(KafkaContractSpec.ReasonCodes.SupplierRejected, payload.GetProperty("reason").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("optionalNote").ValueKind);
                AssertAllPropertiesAllowed(doc.RootElement);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task MixedLifecycle_RollupCompleted_EmitsOrderCompleted_WhenTerminal()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var initCtx = new RentalServiceDbContext(options);
            await initCtx.Database.MigrateAsync();

            Guid supplierId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();

            var clock = new MutableTimeProvider(FixedNow);
            var line1 = OrderTestData.Line(supplierId, "Item 1", "Camera", "Sony", 1, 500_000m, 2_000_000m);
            var line2 = OrderTestData.Line(supplierId, "Item 2", "Camera", "Sony", 1, 500_000m, 2_000_000m);
            var order = OrderTestData.Paid(OrderTestData.Order(renterId, "DH-MIXED-01", FixedNow, line1, line2), FixedNow.AddMinutes(5));

            // Item 1 completes settlement
            var item1 = order.Items.First();
            item1.ApplySupplierAccept(supplierId, FixedNow, "CONF", "op");
            item1.ApplyHandover(FixedNow, "op");
            var hRec = new HandoverRecord(Guid.NewGuid(), item1.Id, FixedNow, null, supplierId, FixedNow, FixedNow);
            item1.SetHandoverRecord(hRec);
            item1.UnitAssignments.First().RecordHandover(hRec.Id, UnitCondition.Normal, null, []);
            // Handover already completed via ApplyHandover and RecordHandover
            item1.SetStatus(RentalItemStatus.Completed);

            // At this point, Item 2 is still PendingSupplierConfirmation -> Order status is Paid (not completed!)
            RentalOrderStatusPolicy.Apply(order, FixedNow, "check");
            Assert.NotEqual(RentalOrderStatus.Completed, order.Status);

            initCtx.RentalOrders.Add(order);
            await initCtx.SaveChangesAsync();

            // Now Item 2 moves to AwaitingHandover
            var item2 = order.Items.Last();
            item2.ApplySupplierAccept(supplierId, FixedNow, "CONF2", "op2");
            await initCtx.SaveChangesAsync();

            // Now cancel Item 2 via ItemCancellationService
            var lockManager = new PostgresOrderLockManager(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = conn }).Build(), NullLogger<PostgresOrderLockManager>.Instance);
            var kafkaOptions = Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = "rental.events.v1" });
            var producer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(initCtx)), kafkaOptions, clock);

            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(initCtx),
                new OperationReceiptRepository(initCtx),
                new OutboxRepository(initCtx),
                lockManager,
                new Mock<IInventoryRentalClient>().Object,
                new Mock<IPaymentRentalClient>().Object,
                new InventoryCommandProducer(new OutboxRepository(initCtx), kafkaOptions, clock),
                initCtx,
                new ConfigurationBuilder().Build(),
                NullLogger<RentalOperationExecutor>.Instance,
                timeProvider: clock,
                rentalEvents: producer);

            var cancelService = new ItemCancellationService(
                new RentalOrderRepository(initCtx),
                lockManager,
                executor,
                new OutboxRepository(initCtx),
                initCtx,
                clock,
                NullLogger<ItemCancellationService>.Instance,
                Options.Create(new HandoverOverdueOptions { AutoExpireGraceHours = 24 }),
                producer);

            clock.SetUtcNow(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
            var cancelRes = await cancelService.CancelByRenterAsync(
                item2.Id,
                new CancelNotDeliveredRequest("No delivery received"),
                "cancel-not-deliv-01",
                OrderTestData.User(order.RenterId));

            Assert.True(cancelRes.IsSuccess, cancelRes.IsFailure ? $"{cancelRes.Error.Code}: {cancelRes.Error.Message}" : "");

            // Verify that OrderCompleted was emitted!
            await using var verifyCtx = new RentalServiceDbContext(options);
            var reloadedOrder = await verifyCtx.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            Assert.Equal(RentalOrderStatus.Completed, reloadedOrder.Status);

            var orderCompletedMsg = await verifyCtx.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderCompleted);
            Assert.NotNull(orderCompletedMsg);

            using var doc = JsonDocument.Parse(orderCompletedMsg.Payload);
            var payload = doc.RootElement.GetProperty("payload");
            Assert.Equal(reloadedOrder.Version, payload.GetProperty("orderVersion").GetInt64());
            AssertAllPropertiesAllowed(doc.RootElement);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReasonCodes_Standardized_AcrossAllCancellationAndExpiryScenarios()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var initCtx = new RentalServiceDbContext(options);
            await initCtx.Database.MigrateAsync();

            var clock = new MutableTimeProvider(FixedNow);
            var kafkaOptions = Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = "rental.events.v1" });
            var producer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(initCtx)), kafkaOptions, clock);

            // 1. Renter Cancelled
            var line1 = OrderTestData.Line(Guid.NewGuid(), "Item 1", "Camera", "Sony", 1, 100m, 200m);
            var order1 = OrderTestData.Order(Guid.NewGuid(), "ORD-CANCEL-1", FixedNow, line1);
            order1.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), order1.Id, $"op-inv-{order1.Id:N}", "CreateInvoice", "fp", FixedNow));
            initCtx.RentalOrders.Add(order1);
            await initCtx.SaveChangesAsync();
            var executor = new RentalOperationExecutor(
                new RentalOrderRepository(initCtx),
                new OperationReceiptRepository(initCtx),
                new OutboxRepository(initCtx),
                new Mock<IOrderLockManager>().Object,
                new Mock<IInventoryRentalClient>().Object,
                new Mock<IPaymentRentalClient>().Object,
                new InventoryCommandProducer(new OutboxRepository(initCtx), kafkaOptions, clock),
                initCtx,
                new ConfigurationBuilder().Build(),
                NullLogger<RentalOperationExecutor>.Instance,
                timeProvider: clock,
                rentalEvents: producer);

            var cancelRes = await executor.ExecuteRenterCancelOrderAsync(order1.Id, order1.RenterId, "Don't want it anymore", "cancel-op-01");
            Assert.True(cancelRes.IsSuccess, cancelRes.IsFailure ? $"{cancelRes.Error.Code}: {cancelRes.Error.Message}" : "");

            // 2. Payment Expired
            var line2 = OrderTestData.Line(Guid.NewGuid(), "Item 2", "Camera", "Sony", 1, 100m, 200m);
            var order2 = OrderTestData.Order(Guid.NewGuid(), "ORD-EXPIRE-2", FixedNow, line2);
            order2.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), order2.Id, $"op-inv-{order2.Id:N}", "CreateInvoice", "fp", FixedNow));
            initCtx.RentalOrders.Add(order2);
            await initCtx.SaveChangesAsync();
            clock.SetUtcNow(order2.PaymentDeadline.AddMinutes(5));
            var paymentExpiryRes = await executor.ExecutePaymentExpiryAsync(order2.Id, "expiry-op-01");
            Assert.True(paymentExpiryRes.IsSuccess, paymentExpiryRes.IsFailure ? $"{paymentExpiryRes.Error.Code}: {paymentExpiryRes.Error.Message}" : "");

            // 3. Supplier confirmation expired
            Guid item3Id = Guid.NewGuid();
            var line3 = OrderTestData.Line(Guid.NewGuid(), "Item 3", "Camera", "Sony", 1, 100m, 200m);
            var order3 = OrderTestData.Paid(OrderTestData.Order(Guid.NewGuid(), "ORD-EXPIRE-3", FixedNow, line3), FixedNow.AddMinutes(5));
            initCtx.RentalOrders.Add(order3);
            await initCtx.SaveChangesAsync();

            await using (var tx3 = await initCtx.Database.BeginTransactionAsync())
            {
                var expItemRes = await producer.EnqueueRentalItemExpiredAsync(
                    new RentalItemExpiredPayload(order3.Id, order3.Items.First().Id, order3.Items.First().SupplierId, FixedNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, 3),
                    correlationId: "supp-exp-01");
                Assert.True(expItemRes.IsSuccess);

                var expHandoverRes = await producer.EnqueueRentalItemExpiredAsync(
                    new RentalItemExpiredPayload(order3.Id, order3.Items.First().Id, Guid.NewGuid(), FixedNow, KafkaContractSpec.ReasonCodes.HandoverExpired, 4),
                    correlationId: "hand-exp-01");
                Assert.True(expHandoverRes.IsSuccess);
                await initCtx.SaveChangesAsync();
                await tx3.CommitAsync();
            }
            // Verify all reasons in DB
            await using var verifyCtx = new RentalServiceDbContext(options);
            var orderCancelled = await verifyCtx.RentalOutboxMessages.FirstAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderCancelled);
            using var cancelDoc = JsonDocument.Parse(orderCancelled.Payload);
            Assert.Equal(KafkaContractSpec.ReasonCodes.RenterCancelled, cancelDoc.RootElement.GetProperty("payload").GetProperty("reason").GetString());

            var orderExpired = await verifyCtx.RentalOutboxMessages.FirstAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderExpired);
            using var expireDoc = JsonDocument.Parse(orderExpired.Payload);
            Assert.Equal(KafkaContractSpec.ReasonCodes.PaymentExpired, expireDoc.RootElement.GetProperty("payload").GetProperty("reason").GetString());

            var itemExpiredMessages = await verifyCtx.RentalOutboxMessages.Where(m => m.MessageType == KafkaContractSpec.EventTypes.RentalItemExpired).ToListAsync();
            Assert.Equal(2, itemExpiredMessages.Count);

            using var suppDoc = JsonDocument.Parse(itemExpiredMessages[0].Payload);
            Assert.Equal(KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, suppDoc.RootElement.GetProperty("payload").GetProperty("reason").GetString());

            using var handDoc = JsonDocument.Parse(itemExpiredMessages[1].Payload);
            Assert.Equal(KafkaContractSpec.ReasonCodes.HandoverExpired, handDoc.RootElement.GetProperty("payload").GetProperty("reason").GetString());
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PrivacyAudit_NeverEmitsSensitiveData_OrUnapprovedFields()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var db = new RentalServiceDbContext(options);
            await db.Database.MigrateAsync();

            var clock = new MutableTimeProvider(FixedNow);
            var kafkaOptions = Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = "rental.events.v1" });
            var producer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(db)), kafkaOptions, clock);

            // Enqueue all domain event types
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid listingId = Guid.NewGuid();
            Guid typeId = Guid.NewGuid();
            Guid brandId = Guid.NewGuid();

            await using (var txPriv = await db.Database.BeginTransactionAsync())
            {
                await producer.EnqueueRentalOrderCreatedAsync(
                    new RentalOrderCreatedPayload(
                        orderId, "DH-001", renterId, "VND", 100m, 200m, 300m, FixedNow.AddMinutes(30), FixedNow,
                        [new RentalOrderCreatedItemPayload(itemId, listingId, supplierId, 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), 100m, 200m, typeId, "Type", brandId, "Brand", "Model")],
                        1));

                await producer.EnqueueRentalOrderPaidAsync(
                    new RentalOrderPaidPayload(orderId, renterId, "INV-001", "PAY-001", 300m, FixedNow, 2));

                await producer.EnqueueRentalItemPaidAsync(
                    new RentalItemPaidPayload(renterId, listingId, typeId, orderId, itemId, 2));

                await producer.EnqueueRentalSupplierConfirmationRequestedAsync(
                    new RentalSupplierConfirmationRequestedPayload(orderId, itemId, supplierId, listingId, 1, FixedNow.AddHours(24), 2));

                await producer.EnqueueRentalItemConfirmedAsync(
                    new RentalItemConfirmedPayload(orderId, itemId, supplierId, FixedNow, "CONF-REF", 3));

                await producer.EnqueueRentalItemRejectedAsync(
                    new RentalItemRejectedPayload(orderId, itemId, supplierId, FixedNow, KafkaContractSpec.ReasonCodes.SupplierRejected, "Private supplier notes", 3));

                await producer.EnqueueRentalItemExpiredAsync(
                    new RentalItemExpiredPayload(orderId, itemId, supplierId, FixedNow, KafkaContractSpec.ReasonCodes.SupplierConfirmationExpired, 3));

                await producer.EnqueueRentalOrderExpiredAsync(
                    new RentalOrderExpiredPayload(orderId, renterId, FixedNow, KafkaContractSpec.ReasonCodes.PaymentExpired, 3));

                await producer.EnqueueRentalItemCancelledAsync(
                    new RentalItemCancelledPayload(orderId, itemId, renterId, "Renter", FixedNow, KafkaContractSpec.ReasonCodes.RenterCancelled, 300m, 3));

                await producer.EnqueueRentalOrderCancelledAsync(
                    new RentalOrderCancelledPayload(orderId, renterId, FixedNow, KafkaContractSpec.ReasonCodes.RenterCancelled, 3));

                await producer.EnqueueRentalItemHandoverCompletedAsync(
                    new RentalItemHandoverCompletedPayload(orderId, itemId, renterId, supplierId, listingId, 1, Guid.NewGuid(), FixedNow, FixedNow, 4));

                await producer.EnqueueRentalUnitsReturnedAsync(
                    new Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload(orderId, itemId, Guid.NewGuid(), FixedNow, FixedNow, [Guid.NewGuid()], 5));

                await producer.EnqueueRentalItemReturnedAsync(
                    new RentalItemReturnedPayload(orderId, itemId, renterId, supplierId, listingId, 1, FixedNow, 6));

                await producer.EnqueueRentalItemCompletedAsync(
                    new RentalItemCompletedPayload(renterId, listingId, typeId, orderId, itemId, 7));

                await producer.EnqueueRentalOrderCompletedAsync(
                    new RentalOrderCompletedPayload(orderId, renterId, FixedNow, 7));

                await db.SaveChangesAsync();
                await txPriv.CommitAsync();
            }

            // Verify ALL outbox records
            var messages = await db.RentalOutboxMessages.ToListAsync();
            Assert.NotEmpty(messages);

            string[] forbiddenTokens = ["090", "bank", "account", "Private", "Street", "Sensitive", "password", "secret", "serialNumber", "assetCode"];

            foreach (var msg in messages)
            {
                using var doc = JsonDocument.Parse(msg.Payload);
                AssertAllPropertiesAllowed(doc.RootElement);

                foreach (var forbidden in forbiddenTokens)
                {
                    Assert.DoesNotContain($"\"{forbidden}\"", msg.Payload, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Replay_DoesNotProduceNewLogicalEvents_OrChangeVersion()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync()) return;
        var (conn, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(conn);
            await using var db = new RentalServiceDbContext(options);
            await db.Database.MigrateAsync();

            var clock = new MutableTimeProvider(FixedNow);
            var kafkaOptions = Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = "rental.events.v1" });
            var producer = new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(db)), kafkaOptions, clock);

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();

            var payload = new RentalOrderPaidPayload(orderId, renterId, "INV-001", "PAY-001", 500_000m, FixedNow, 2);

            // First emission
            await using (var tx1 = await db.Database.BeginTransactionAsync())
            {
                var res1 = await producer.EnqueueRentalOrderPaidAsync(payload, correlationId: "op-replay");
                Assert.True(res1.IsSuccess);
                Assert.False(res1.Value.IsReplay);
                await db.SaveChangesAsync();
                await tx1.CommitAsync();
            }

            int countBefore = await db.RentalOutboxMessages.CountAsync();
            var firstMessage = await db.RentalOutboxMessages.SingleAsync();

            // Replay with same logical operation
            await using (var tx2 = await db.Database.BeginTransactionAsync())
            {
                var res2 = await producer.EnqueueRentalOrderPaidAsync(payload, correlationId: "op-replay-diff-key");
                Assert.True(res2.IsSuccess);
                Assert.True(res2.Value.IsReplay);
                Assert.Equal(firstMessage.Id, res2.Value.MessageId);
                await db.SaveChangesAsync();
                await tx2.CommitAsync();
            }

            var reloadedMessage = await db.RentalOutboxMessages.SingleAsync();
            Assert.Equal(firstMessage.Id, reloadedMessage.Id);
            Assert.Equal(firstMessage.Payload, reloadedMessage.Payload);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private static void AssertAllPropertiesAllowed(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(AllowedFieldNames.Contains(property.Name), $"Field '{property.Name}' is not in the allowed schema whitelist.");
                AssertAllPropertiesAllowed(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertAllPropertiesAllowed(item);
            }
        }
    }
}
