using System.Text.Json;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
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

namespace Dprental.RentalService.Api.Tests.Payments;

public sealed class ExtendHoldCallbackPostgreSqlTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
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
            $"DH-EXT-{orderId:N}"[..16],
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

    private static (PaymentEventsDispatcher Dispatcher, FakeOrderHubNotifier Notifier, TimeProvider Time) CreateDispatcher(
        RentalServiceDbContext db,
        IPaymentRentalClient? paymentClient = null,
        DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? FixedNow;
        var timeProvider = new FixedTimeProvider(effectiveNow);
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

        var allocHandler = new InventoryAllocationResultHandler(
            orders, db, lockManager, timeProvider, null, cmdProducer, null,
            NullLogger<InventoryAllocationResultHandler>.Instance, eventProducer, paymentClient);

        var refundConsumer = new RefundCompletedConsumer(
            orders, inboxes, lockManager, db, NullLogger<RefundCompletedConsumer>.Instance, timeProvider);

        var dispatcher = new PaymentEventsDispatcher(
            inboxes, orders, lockManager, paymentWorkflow,
            refundConsumer,
            new KafkaOutboxWriter(outboxes), db,
            allocHandler,
            kafkaOptions,
            null,
            NullLogger<PaymentEventsDispatcher>.Instance,
            timeProvider,
            null,
            null,
            notifier);

        return (dispatcher, notifier, timeProvider);
    }

    [Fact]
    public async Task SingleItemOrder_InvoicePaid_ThenExtendHoldSuccess_PersistsReceiptWithoutRepeatingPayment()
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

            // 1. Seed Order in PostgreSQL
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                order.AttachInvoice("INV-P1-001", FixedNow);
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // 2. Dispatch real InvoicePaid event
            string invoiceRef = "INV-P1-001";
            string paymentRef = "PAY-P1-001";
            long versionAfterPaid;

            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(paidDb);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, invoiceRef, paymentRef,
                    9_000_000m, "VND", FixedNow);
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-paid-1", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-paid-1", null, envJson, FixedNow, "fp-paid-1");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: FixedNow), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var processRes = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(processRes.IsSuccess, processRes.IsFailure ? processRes.Error.Message : null);
                Assert.Single(notifier.RenterNotifications);
            }

            // 3. Inspect committed state in fresh DbContext
            string extendOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{itemId}");
            await using (var verifyPaidDb = new RentalServiceDbContext(dbOptions))
            {
                var paidOrder = await verifyPaidDb.RentalOrders
                    .Include(o => o.OperationReceipts)
                    .SingleAsync(o => o.Id == orderId);

                Assert.Equal(RentalOrderStatus.Paid, paidOrder.Status);
                versionAfterPaid = paidOrder.Version;
                var receipt = Assert.Single(paidOrder.OperationReceipts, r => r.OperationId == extendOpKey);
                Assert.Equal(RentalOperationReceipt.StatusPending, receipt.Status);
            }

            // 4. Dispatch ExtendHold success callback
            Guid extendInboxId = Guid.NewGuid();
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);

                var extendSuccessBody = JsonSerializer.Serialize(new
                {
                    receiptRef = "rcpt-inv-01",
                    allocationGroupId = $"rental-item:{itemId}",
                    newHoldExpiresAt = FixedNow.AddHours(24)
                });

                var extendCallbackPayload = new
                {
                    commandType = "ExtendHold",
                    operationKey = extendOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = itemId,
                    success = true,
                    payload = JsonDocument.Parse(extendSuccessBody).RootElement
                };

                var callbackEnv = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = FixedNow.AddSeconds(10),
                    correlationId = "corr-ext-1",
                    causationId = (Guid?)null,
                    payload = extendCallbackPayload
                };
                string callbackEnvJson = JsonSerializer.Serialize(callbackEnv, JsonOptions);

                var inbox = new RentalInboxMessage(
                    extendInboxId, callbackEnv.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-ext-1", null,
                    callbackEnvJson, FixedNow.AddSeconds(10), "fp-ext-1");

                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var callbackRes = await dispatcher.ProcessInboxMessageAsync(extendInboxId);
                Assert.True(callbackRes.IsSuccess, callbackRes.IsFailure ? callbackRes.Error.Message : null);

                // Receipt-only mutation MUST NOT emit new payment notifications
                Assert.Empty(notifier.RenterNotifications);
                Assert.Empty(notifier.SupplierNotifications);
            }

            // 5. Verify via a completely NEW DbContext that receipt is Succeeded and version NOT bumped
            await using (var freshDb = new RentalServiceDbContext(dbOptions))
            {
                var inboxMsg = await freshDb.RentalInboxMessages.SingleAsync(m => m.Id == extendInboxId);
                Assert.Equal(RentalInboxMessage.StatusProcessed, inboxMsg.Status);

                var finalOrder = await freshDb.RentalOrders
                    .Include(o => o.OperationReceipts)
                    .SingleAsync(o => o.Id == orderId);

                Assert.Equal(RentalOrderStatus.Paid, finalOrder.Status);
                Assert.Equal(versionAfterPaid, finalOrder.Version); // Version MUST remain unchanged!

                var extendReceipt = Assert.Single(finalOrder.OperationReceipts, r => r.OperationId == extendOpKey);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, extendReceipt.Status);

                // Verify outbox has only ONE RentalOrderPaid (from initial payment, NOT duplicated by callback)
                var outboxes = await freshDb.RentalOutboxMessages.ToListAsync();
                Assert.Single(outboxes, m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderPaid);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task MultiItemOrder_ProcessesEachExtendHoldReply_FinalReplyDoesNotRepeatPayment()
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

            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (item1Id, supplierA), (item2Id, supplierB));
                order.AttachInvoice("INV-P1-MULTI", FixedNow);
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Pay order
            long versionAfterPaid;
            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, _, _) = CreateDispatcher(paidDb);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-P1-MULTI", "PAY-P1-MULTI",
                    18_000_000m, "VND", FixedNow);
                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), FixedNow, "corr-paid-m", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-paid-m", null, envJson, FixedNow, "fp-paid-m");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: FixedNow), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);
            }

            string opKey1 = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{item1Id}");
            string opKey2 = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{item2Id}");

            await using (var checkDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await checkDb.RentalOrders.SingleAsync(o => o.Id == orderId);
                versionAfterPaid = order.Version;
            }

            // Reply 1 for Item 1
            await using (var reply1Db = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(reply1Db);
                var body = JsonSerializer.Serialize(new { receiptRef = "r1", allocationGroupId = "g1", newHoldExpiresAt = FixedNow.AddHours(24) });
                var callbackPayload = new { commandType = "ExtendHold", operationKey = opKey1, rentalOrderId = orderId, rentalItemId = item1Id, success = true, payload = JsonDocument.Parse(body).RootElement };
                var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddSeconds(5), correlationId = "corr-m-1", causationId = (Guid?)null, payload = callbackPayload };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-m-1", null, envJson, FixedNow.AddSeconds(5), "fp-m-1");
                reply1Db.RentalInboxMessages.Add(inbox);
                await reply1Db.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);
                Assert.Empty(notifier.RenterNotifications);
            }

            // Check item 1 Succeeded, item 2 still Pending
            await using (var verifyMidDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await verifyMidDb.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == orderId);
                Assert.Equal(versionAfterPaid, order.Version);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, order.OperationReceipts.Single(r => r.OperationId == opKey1).Status);
                Assert.Equal(RentalOperationReceipt.StatusPending, order.OperationReceipts.Single(r => r.OperationId == opKey2).Status);
            }

            // Reply 2 for Item 2 (FINAL REPLY)
            Guid finalInboxId = Guid.NewGuid();
            await using (var reply2Db = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(reply2Db);
                var body = JsonSerializer.Serialize(new { receiptRef = "r2", allocationGroupId = "g2", newHoldExpiresAt = FixedNow.AddHours(24) });
                var callbackPayload = new { commandType = "ExtendHold", operationKey = opKey2, rentalOrderId = orderId, rentalItemId = item2Id, success = true, payload = JsonDocument.Parse(body).RootElement };
                var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddSeconds(10), correlationId = "corr-m-2", causationId = (Guid?)null, payload = callbackPayload };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(finalInboxId, env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-m-2", null, envJson, FixedNow.AddSeconds(10), "fp-m-2");
                reply2Db.RentalInboxMessages.Add(inbox);
                await reply2Db.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(finalInboxId);
                Assert.True(res.IsSuccess, res.IsFailure ? res.Error.Message : null);
                Assert.Empty(notifier.RenterNotifications);
            }

            // Final check in new DbContext: both receipts Succeeded, version untouched, exactly 1 order paid outbox
            await using (var finalDb = new RentalServiceDbContext(dbOptions))
            {
                var inboxMsg = await finalDb.RentalInboxMessages.SingleAsync(m => m.Id == finalInboxId);
                Assert.Equal(RentalInboxMessage.StatusProcessed, inboxMsg.Status);

                var order = await finalDb.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == orderId);
                Assert.Equal(RentalOrderStatus.Paid, order.Status);
                Assert.Equal(versionAfterPaid, order.Version);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, order.OperationReceipts.Single(r => r.OperationId == opKey1).Status);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, order.OperationReceipts.Single(r => r.OperationId == opKey2).Status);

                var outboxes = await finalDb.RentalOutboxMessages.ToListAsync();
                Assert.Single(outboxes, m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderPaid);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CallbackArrivesAfterOrderOrItemAdvanced_PersistsReceiptWithoutRevivingOrResetting()
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
            string extendOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{itemId}");

            // 1. Seed order already Paid and supplier has accepted (item is AwaitingHandover)
            DateTimeOffset initialDeadline;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                order.AttachInvoice("INV-ADV-1", FixedNow);
                order.MarkAsPaid("INV-ADV-1", "PAY-ADV-1", FixedNow, FixedNow);

                var item = order.Items.Single();
                initialDeadline = item.SupplierConfirmationDeadline!.Value;
                // Supplier accepts -> item advances to AwaitingHandover
                item.ApplySupplierAccept(supplierId, FixedNow.AddHours(1), "CONF-001", "op-sup-accept");

                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, extendOpKey, "ExtendHold",
                    RentalOperationExecutor.ComputeFingerprint($"ExtendHold:{item.AllocationGroupId}"),
                    FixedNow, itemId, "{}");
                order.AddOperationReceipt(receipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            long versionBeforeCallback;
            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var o = await verifyDb.RentalOrders.SingleAsync(x => x.Id == orderId);
                versionBeforeCallback = o.Version;
            }

            // 2. Late ExtendHold callback arrives
            Guid inboxId = Guid.NewGuid();
            await using (var callbackDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(callbackDb);
                var body = JsonSerializer.Serialize(new { receiptRef = "r-late", allocationGroupId = "g-late", newHoldExpiresAt = FixedNow.AddHours(48) });
                var callbackPayload = new { commandType = "ExtendHold", operationKey = extendOpKey, rentalOrderId = orderId, rentalItemId = itemId, success = true, payload = JsonDocument.Parse(body).RootElement };
                var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddHours(2), correlationId = "corr-late", causationId = (Guid?)null, payload = callbackPayload };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(inboxId, env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-late", null, envJson, FixedNow.AddHours(2), "fp-late");
                callbackDb.RentalInboxMessages.Add(inbox);
                await callbackDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess, res.IsFailure ? res.Error.Message : null);
                Assert.Empty(notifier.RenterNotifications);
            }

            // 3. Verify in fresh DbContext: receipt Succeeded, item still AwaitingHandover, deadline NOT reset, version NOT changed
            await using (var freshDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await freshDb.RentalOrders
                    .Include(o => o.Items)
                    .Include(o => o.OperationReceipts)
                    .SingleAsync(o => o.Id == orderId);

                var item = order.Items.Single();
                Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status); // Status NOT rolled back or modified
                Assert.Equal(initialDeadline, item.SupplierConfirmationDeadline); // Deadline NOT reset
                Assert.Equal(versionBeforeCallback, order.Version); // Version NOT bumped

                var rcpt = order.OperationReceipts.Single(r => r.OperationId == extendOpKey);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, rcpt.Status);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DuplicateCallbackSameResult_SucceedsIdempotent_ConflictingTerminalResult_Fails()
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
            string extendOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{orderId}:{itemId}");

            var callbackPayloadObj = new
            {
                commandType = "ExtendHold",
                operationKey = extendOpKey,
                rentalOrderId = orderId,
                rentalItemId = itemId,
                success = true,
                payload = new { receiptRef = "r-dup", allocationGroupId = "g-dup", newHoldExpiresAt = FixedNow.AddHours(24) }
            };
            string responseJson = JsonSerializer.Serialize(callbackPayloadObj, JsonOptions);

            // Seed order with Succeeded receipt
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                order.AttachInvoice("INV-DUP-1", FixedNow);
                order.MarkAsPaid("INV-DUP-1", "PAY-DUP-1", FixedNow, FixedNow);

                var receipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, extendOpKey, "ExtendHold",
                    RentalOperationExecutor.ComputeFingerprint($"ExtendHold:{order.Items.Single().AllocationGroupId}"),
                    FixedNow, itemId, "{}");
                receipt.MarkSucceeded(FixedNow, responseJson);
                order.AddOperationReceipt(receipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // 1. Replay identical success callback -> Succeeded idempotent
            await using (var replayDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, _, _) = CreateDispatcher(replayDb);
                var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddSeconds(1), correlationId = "corr-dup", causationId = (Guid?)null, payload = callbackPayloadObj };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-dup", null, envJson, FixedNow.AddSeconds(1), "fp-dup");
                replayDb.RentalInboxMessages.Add(inbox);
                await replayDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess, res.IsFailure ? res.Error.Message : null);
            }

            // 2. Conflicting callback: Failure arriving for already Succeeded receipt -> Conflict error!
            await using (var conflictDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, _, _) = CreateDispatcher(conflictDb);
                var failBody = JsonSerializer.Serialize(new { errorCode = "HOLD_EXPIRED", errorMessage = "Expired" }, JsonOptions);
                var callbackPayload = new { commandType = "ExtendHold", operationKey = extendOpKey, rentalOrderId = orderId, rentalItemId = itemId, success = false, payload = JsonDocument.Parse(failBody).RootElement };
                var env = new { messageId = Guid.NewGuid(), messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, version = 1, producer = KafkaContractSpec.Producers.InventoryService, aggregateId = orderId.ToString("D"), occurredAt = FixedNow.AddSeconds(2), correlationId = "corr-conf", causationId = (Guid?)null, payload = callbackPayload };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(Guid.NewGuid(), env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-conf", null, envJson, FixedNow.AddSeconds(2), "fp-conf");

                // Direct handler returns INVENTORY_RESULT_CONFLICT
                var allocHandler = new InventoryAllocationResultHandler(
                    new RentalOrderRepository(conflictDb), conflictDb, new FakeOrderLockManager(), new FixedTimeProvider(FixedNow));
                var order = await conflictDb.RentalOrders.Include(o => o.Items).Include(o => o.OperationReceipts).SingleAsync(o => o.Id == orderId);
                var directRes = await allocHandler.HandleAllocationEventAsync(orderId, inbox, order, CancellationToken.None);
                Assert.True(directRes.IsFailure);
                Assert.Equal("INVENTORY_RESULT_CONFLICT", directRes.Error.Code);

                // Dispatcher marks message DeadLettered with INVENTORY_RESULT_CONFLICT
                conflictDb.RentalInboxMessages.Add(inbox);
                await conflictDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                var deadLettered = await conflictDb.RentalInboxMessages.SingleAsync(m => m.Id == inbox.Id);
                Assert.Equal(RentalInboxMessage.StatusDeadLettered, deadLettered.Status);
                Assert.Contains("INVENTORY_RESULT_CONFLICT", deadLettered.ErrorMessage);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private sealed class AcceptLatePaymentMockClient : IPaymentRentalClient
    {
        public Task<Result<RentalRefundSyncResponse>> SynchronizeRefundAsync(
            RentalRefundSyncRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("Refund synchronization is not used by this fixture.");

        public bool Called { get; private set; }
        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<AcceptLatePaymentResponse>> AcceptLatePaymentAsync(string invoiceRef, AcceptLatePaymentRequest request, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(Result.Success(new AcceptLatePaymentResponse(invoiceRef, "PAID", false, DateTimeOffset.UtcNow)));
        }
    }

    [Fact]
    public async Task ValidRecoveryHold_CompletesAndEmitsPaidAtBoundary()
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
            string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{orderId}:{itemId}");

            // Seed order in PendingPayment, with payment received after deadline (recovery flow)
            DateTimeOffset latePaidAt = FixedNow.AddMinutes(5);
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, (itemId, supplierId));
                order.AttachInvoice("INV-REC-OK", FixedNow);
                // Recorded payment received but NOT marked paid yet (status is PendingPayment)
                order.RecordPaymentReceived("INV-REC-OK", "PAY-REC-OK", latePaidAt, latePaidAt);

                var recReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(), orderId, recOpKey, "RecoveryHold",
                    RentalOperationExecutor.ComputeFingerprint($"RecoveryHold:{orderId}:{itemId}"),
                    latePaidAt, itemId, "{}");
                order.AddOperationReceipt(recReceipt);

                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            var paymentClient = new AcceptLatePaymentMockClient();

            // Dispatch RecoveryHold success callback
            Guid recInboxId = Guid.NewGuid();
            await using (var recDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(recDb, paymentClient, now: latePaidAt.AddSeconds(5));
                var allocList = new[] { new { allocationId = Guid.NewGuid(), assetId = Guid.NewGuid(), deviceCode = "DEV-REC-01" } };
                var body = JsonSerializer.Serialize(new
                {
                    receiptRef = "rcpt-rec-01",
                    allocationGroupId = $"rental-item:{itemId}:rec-1",
                    allocations = allocList
                });

                var callbackPayload = new
                {
                    commandType = "CreateHold",
                    operationKey = recOpKey,
                    rentalOrderId = orderId,
                    rentalItemId = itemId,
                    success = true,
                    payload = JsonDocument.Parse(body).RootElement
                };

                var env = new
                {
                    messageId = Guid.NewGuid(),
                    messageType = KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                    version = 1,
                    producer = KafkaContractSpec.Producers.InventoryService,
                    aggregateId = orderId.ToString("D"),
                    occurredAt = latePaidAt.AddSeconds(5),
                    correlationId = "corr-rec-ok",
                    causationId = (Guid?)null,
                    payload = callbackPayload
                };
                string envJson = JsonSerializer.Serialize(env, JsonOptions);
                var inbox = new RentalInboxMessage(recInboxId, env.messageId, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, KafkaContractSpec.Producers.InventoryService, orderId.ToString("D"), "corr-rec-ok", null, envJson, latePaidAt.AddSeconds(5), "fp-rec-ok");
                recDb.RentalInboxMessages.Add(inbox);
                await recDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(recInboxId);
                Assert.True(res.IsSuccess, res.IsFailure ? res.Error.Message : null);

                // Recovery completion boundary MUST emit PAYMENT_CONFIRMED
                Assert.Single(notifier.RenterNotifications);
                Assert.Equal(OrderChangeTypes.PaymentConfirmed, notifier.RenterNotifications.Single().Notification.ChangeType);
            }

            Assert.True(paymentClient.Called);

            // Verify PostgreSQL persisted state in new DbContext
            await using (var freshDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await freshDb.RentalOrders
                    .Include(o => o.Items)
                    .Include(o => o.OperationReceipts)
                    .SingleAsync(o => o.Id == orderId);

                Assert.Equal(RentalOrderStatus.Paid, order.Status);
                Assert.False(order.LatePaymentRefundRequired);
                Assert.All(order.Items, i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));

                var rcpt = order.OperationReceipts.Single(r => r.OperationId == recOpKey);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded, rcpt.Status);

                var outboxes = await freshDb.RentalOutboxMessages.ToListAsync();
                Assert.Contains(outboxes, m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderPaid);
                Assert.Contains(outboxes, m => m.MessageType == KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
