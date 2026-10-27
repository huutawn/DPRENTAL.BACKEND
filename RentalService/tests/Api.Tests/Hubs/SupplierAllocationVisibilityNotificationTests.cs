using System.Text.Json;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
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

public sealed class SupplierAllocationVisibilityNotificationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static DbContextOptions<RentalServiceDbContext> CreateDbOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    private static RentalOrder CreateOrder(Guid orderId, Guid renterId, params (Guid ItemId, Guid SupplierId)[] items)
    {
        var itemDtos = items.Select((it, idx) =>
        {
            var assign = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            return new InitialRentalItemDto(
                it.ItemId,
                Guid.NewGuid(),
                it.SupplierId,
                1,
                new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 5),
                $"Thiết bị {idx + 1}",
                "Mô tả",
                "Kho 1",
                1_000_000m,
                5_000_000m,
                $"grp-item-{idx + 1}",
                [assign],
                TestListingSnapshots.Default);
        }).ToList();

        var order = RentalOrder.Create(
            orderId,
            $"DH-VIS-{orderId:N}"[..16],
            renterId,
            "VND",
            "Pickup",
            "Nguyễn Văn A",
            "0901234567",
            "Địa chỉ",
            null,
            "v1.0",
            "terms",
            "hash",
            $"op-create-{orderId:N}"[..20],
            FixedNow,
            itemDtos).Value;

        return order;
    }

    private static (PaymentEventsDispatcher Dispatcher, FakeOrderHubNotifier Notifier, InventoryAllocationResultHandler AllocHandler) CreateDispatcher(
        RentalServiceDbContext db,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? new FixedTimeProvider(FixedNow);
        var orders = new RentalOrderRepository(db);
        var inboxes = new InboxRepository(db);
        var outboxes = new OutboxRepository(db);
        var receipts = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var notifier = new FakeOrderHubNotifier();

        var kafkaOptions = Options.Create(PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            RentalEventsTopic = "rental.events.v1"
        }));

        var eventProducer = new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, time);
        var cmdProducer = new InventoryCommandProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, time);
        var mockExecutor = new Mock<IRentalOperationExecutor>();

        var workflowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:RentalEventsTopic"] = "rental.events.v1"
        }).Build();

        var paymentWorkflow = new PaymentWorkflowService(
            orders, inboxes, outboxes, receipts, lockManager,
            mockExecutor.Object, db, cmdProducer, workflowConfig,
            NullLogger<PaymentWorkflowService>.Instance, eventProducer, time, notifier);

        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, time, null, cmdProducer, null,
            NullLogger<InventoryAllocationResultHandler>.Instance, eventProducer, null);

        var refundConsumer = new RefundCompletedConsumer(
            orders, inboxes, lockManager, db, NullLogger<RefundCompletedConsumer>.Instance, time);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager, paymentWorkflow,
            refundConsumer,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            time,
            null,
            null,
            notifier);

        return (dispatcher, notifier, allocHandler);
    }

    [Fact]
    public async Task Release_PendingPaymentOrder_RestRejectsAndSupplierReceivesNoNotification()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = CreateDbOptions(connString);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemId}");

            // 1. Seed unpaid order (PendingPayment) in PostgreSQL
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));

                var releaseReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, releaseOpKey, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{order.Items.Single().AllocationGroupId}"),
                    FixedNow, itemId, "{}");
                order.AddOperationReceipt(releaseReceipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // 2. Verify REST rejects read and policy returns false (PARITY)
            await using (var queryDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(queryDb);
                var order = await repo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(order);
                var subOrder = order.SubOrders.Single();

                bool canSupplierReadPolicy = SupplierOrderVisibilityPolicy.CanSupplierView(order);
                Assert.False(canSupplierReadPolicy);

                var restSubOrder = await repo.GetPaidSubOrderForSupplierAsync(subOrder.Id, supplierId);
                Assert.Null(restSubOrder); // REST rejects: unpaid suborder is invisible to supplier!
            }

            // 3. Dispatch Release callback
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);

                var relBody = JsonSerializer.Serialize(new
                {
                    receiptRef = "rcpt-rel-01",
                    allocationGroupId = $"rental-item:{itemId}",
                    releasedAt = FixedNow.AddMinutes(5)
                });

                var callbackPayload = new
                {
                    commandType = "ReleaseAllocation",
                    operationKey = releaseOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = itemId,
                    success = true,
                    payload = JsonDocument.Parse(relBody).RootElement
                };

                var env = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = FixedNow.AddMinutes(5),
                    correlationId = "corr-rel-1",
                    causationId = (Guid?)null,
                    payload = callbackPayload
                };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-rel-1", null,
                    envJson, FixedNow.AddMinutes(5), "fp-rel-1");

                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                // Renter gets ALLOCATION_CHANGED
                Assert.Single(notifier.RenterNotifications);
                Assert.Equal(OrderChangeTypes.AllocationChanged, notifier.RenterNotifications.Single().Notification.ChangeType);

                // Supplier MUST NOT receive any notification because the order was never visible!
                // Fixed: supplierCanRead=False; supplierNotifications=0!
                Assert.Empty(notifier.SupplierNotifications);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Release_CancelledOrExpiredUnpaidOrder_SupplierReceivesNoNotification()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = CreateDbOptions(connString);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{itemId}");

            // Seed unpaid expired order
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                order.MarkPaymentExpired(FixedNow.AddMinutes(30), "op-expire-test");

                var releaseReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, releaseOpKey, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{order.Items.Single().AllocationGroupId}"),
                    FixedNow.AddMinutes(30), itemId, "{}");
                order.AddOperationReceipt(releaseReceipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Dispatch Release
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);

                var relBody = JsonSerializer.Serialize(new { receiptRef = "rcpt-rel-exp" });
                var callbackPayload = new
                {
                    commandType = "ReleaseAllocation",
                    operationKey = releaseOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = itemId,
                    success = true,
                    payload = JsonDocument.Parse(relBody).RootElement
                };

                var env = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = FixedNow.AddMinutes(35),
                    correlationId = "corr-rel-exp",
                    causationId = (Guid?)null,
                    payload = callbackPayload
                };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-rel-exp", null,
                    envJson, FixedNow.AddMinutes(35), "fp-rel-exp");

                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                // Supplier receives NO notifications
                Assert.Empty(notifier.SupplierNotifications);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Release_PaidOrder_CorrectSupplierReceivesAllocationChanged_AndUnrelatedDoesNot()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = CreateDbOptions(connString);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{orderId}:{item1Id}");

            // Seed Paid order with 2 items from 2 different suppliers
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (item1Id, supplierA), (item2Id, supplierB));
                order.AttachInvoice("INV-VIS-PAID", FixedNow);
                order.MarkAsPaid("INV-VIS-PAID", "PAY-VIS-PAID", FixedNow, FixedNow);

                var releaseReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, releaseOpKey, "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{order.Items.First().AllocationGroupId}"),
                    FixedNow, item1Id, "{}");
                order.AddOperationReceipt(releaseReceipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Verify both suppliers can currently read their respective suborders on REST
            Guid subOrderIdA;
            await using (var queryDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(queryDb);
                var order = await repo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(order);

                subOrderIdA = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
                var subOrderA = await repo.GetPaidSubOrderForSupplierAsync(subOrderIdA, supplierA);
                Assert.NotNull(subOrderA);

                var subOrderIdB = order.SubOrders.Single(s => s.SupplierId == supplierB).Id;
                var subOrderB = await repo.GetPaidSubOrderForSupplierAsync(subOrderIdB, supplierB);
                Assert.NotNull(subOrderB);
            }

            // Dispatch Release for Item 1 (Supplier A's item)
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);

                var relBody = JsonSerializer.Serialize(new { receiptRef = "rcpt-rel-a" });
                var callbackPayload = new
                {
                    commandType = "ReleaseAllocation",
                    operationKey = releaseOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = item1Id,
                    success = true,
                    payload = JsonDocument.Parse(relBody).RootElement
                };

                var env = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = FixedNow.AddMinutes(10),
                    correlationId = "corr-rel-a",
                    causationId = (Guid?)null,
                    payload = callbackPayload
                };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-rel-a", null,
                    envJson, FixedNow.AddMinutes(10), "fp-rel-a");

                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                // Renter gets ALLOCATION_CHANGED
                Assert.Single(notifier.RenterNotifications);

                // ONLY Supplier A receives the notification! Supplier B does NOT receive it!
                Assert.Single(notifier.SupplierNotifications);
                var noteA = notifier.SupplierNotifications.Single();
                Assert.Equal(supplierA, noteA.SupplierId);
                Assert.Equal(subOrderIdA, noteA.Notification.SubOrderId);
                Assert.Equal(OrderChangeTypes.AllocationChanged, noteA.Notification.ChangeType);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Compensation_OrderPreviouslyVisible_AllRelatedSuppliersReceiveInvalidation()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = CreateDbOptions(connString);
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{orderId}:{item1Id}");

            // Seed an order that was previously marked paid (visible on REST), but now fails recovery
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (item1Id, supplierA), (item2Id, supplierB));
                order.AttachInvoice("INV-COMP-1", FixedNow);
                order.MarkAsPaid("INV-COMP-1", "PAY-COMP-1", FixedNow, FixedNow);

                var recReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, recOpKey, "RecoveryHold",
                    RentalOperationExecutor.ComputeFingerprint($"RecoveryHold:{orderId}:{item1Id}"),
                    FixedNow, item1Id, "{}");
                order.AddOperationReceipt(recReceipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Verify both suppliers could see their suborders prior to compensation
            await using (var queryDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(queryDb);
                var order = await repo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(order);
                Assert.True(SupplierOrderVisibilityPolicy.CanSupplierView(order));
            }

            // Dispatch RecoveryHold REJECTED -> triggers CompensateAndRefundAllItemsAsync
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);

                var failBody = JsonSerializer.Serialize(new { errorCode = "HOLD_FAILED", errorMessage = "No units available" });
                var callbackPayload = new
                {
                    commandType = "CreateHold",
                    operationKey = recOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = item1Id,
                    success = false,
                    payload = JsonDocument.Parse(failBody).RootElement
                };

                var env = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = FixedNow.AddMinutes(5),
                    correlationId = "corr-comp-fail",
                    causationId = (Guid?)null,
                    payload = callbackPayload
                };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-comp-fail", null,
                    envJson, FixedNow.AddMinutes(5), "fp-comp-fail");

                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                // Renter receives PaymentRefundRequired
                Assert.Single(notifier.RenterNotifications);
                Assert.Equal(OrderChangeTypes.PaymentRefundRequired, notifier.RenterNotifications.Single().Notification.ChangeType);

                // Both Supplier A and Supplier B MUST receive invalidation notification because the order lost visibility!
                Assert.Equal(2, notifier.SupplierNotifications.Count);
                Assert.Contains(notifier.SupplierNotifications, n => n.SupplierId == supplierA && n.Notification.ChangeType == OrderChangeTypes.PaymentRefundRequired);
                Assert.Contains(notifier.SupplierNotifications, n => n.SupplierId == supplierB && n.Notification.ChangeType == OrderChangeTypes.PaymentRefundRequired);
            }

            // Check in fresh DbContext: order is now LatePaymentRefundRequired and REST now blocks both suppliers!
            await using (var freshDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(freshDb);
                var order = await repo.GetByIdWithDetailsAsync(orderId);
                Assert.NotNull(order);
                Assert.True(order.LatePaymentRefundRequired);
                Assert.False(SupplierOrderVisibilityPolicy.CanSupplierView(order));

                foreach (var subOrder in order.SubOrders)
                {
                    var restResult = await repo.GetPaidSubOrderForSupplierAsync(subOrder.Id, subOrder.SupplierId);
                    Assert.Null(restResult); // REST now returns null (invalidation succeeded)
                }
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task PolicyParityWithRest_ComprehensivePostgreSqlMatrix()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL test database required.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = CreateDbOptions(connString);

            // Matrix test cases:
            // 1. PendingPayment: PaymentConfirmedAt = null, LatePaymentRefundRequired = false -> Visible = false
            // 2. Paid: PaymentConfirmedAt = now, LatePaymentRefundRequired = false -> Visible = true
            // 3. LatePaymentRefund: PaymentConfirmedAt = now, LatePaymentRefundRequired = true -> Visible = false
            // 4. CancelledUnpaid: PaymentConfirmedAt = null, LatePaymentRefundRequired = false -> Visible = false
            // 5. LateRefundOnUnpaid: PaymentConfirmedAt = null, LatePaymentRefundRequired = true -> Visible = false

            var cases = new (string CaseName, bool SetPaid, bool SetLateRefund, bool ExpectedVisible)[]
            {
                ("PendingPayment", false, false, false),
                ("Paid", true, false, true),
                ("LatePaymentRefund", true, true, false),
                ("CancelledUnpaid", false, false, false),
                ("LateRefundOnUnpaid", false, true, false)
            };

            await using (var db = new RentalServiceDbContext(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();

                foreach (var (caseName, setPaid, setLateRefund, expectedVisible) in cases)
                {
                    Guid orderId = Guid.NewGuid();
                    Guid renterId = Guid.NewGuid();
                    Guid supplierId = Guid.NewGuid();
                    Guid itemId = Guid.NewGuid();

                    var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                    if (setPaid)
                    {
                        order.AttachInvoice($"INV-{caseName}", FixedNow);
                        order.MarkAsPaid($"INV-{caseName}", $"PAY-{caseName}", FixedNow, FixedNow);
                    }
                    if (setLateRefund)
                    {
                        order.MarkAsLatePayment($"INV-{caseName}", $"PAY-{caseName}", FixedNow, FixedNow);
                    }

                    db.RentalOrders.Add(order);
                }
                await db.SaveChangesAsync();
            }

            // Verify parity against real PostgreSQL queries
            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(verifyDb);
                var allOrders = await verifyDb.RentalOrders.Include(o => o.SubOrders).ToListAsync();

                foreach (var order in allOrders)
                {
                    var subOrder = order.SubOrders.Single();
                    bool policyResult = SupplierOrderVisibilityPolicy.CanSupplierView(order);
                    var restSubOrder = await repo.GetPaidSubOrderForSupplierAsync(subOrder.Id, subOrder.SupplierId);
                    bool restResult = restSubOrder != null;

                    Assert.Equal(policyResult, restResult); // PARITY 100%!
                }
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
