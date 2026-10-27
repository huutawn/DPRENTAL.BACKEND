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

public sealed class LatePaymentRefundVersionAdvancementPostgreSqlTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static DbContextOptions<RentalServiceDbContext> CreateDbOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    private static RentalOrder CreateOrder(Guid orderId, Guid renterId, Guid itemId, Guid supplierId)
    {
        var assign = new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var itemDto = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 5),
            "Máy xúc 01",
            "Mô tả",
            "Kho 1",
            1_000_000m,
            5_000_000m,
            $"grp-item-1",
            [assign],
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            $"DH-VER-{orderId:N}"[..16],
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
            [itemDto]).Value;

        return order;
    }

    private static (PaymentEventsDispatcher Dispatcher, FakeOrderHubNotifier Notifier, TimeProvider Time) CreateDispatcher(
        RentalServiceDbContext db,
        DateTimeOffset? now = null,
        IRentalEventProducer? customEventProducer = null)
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

        var eventProducer = customEventProducer ?? new RentalEventProducer(new KafkaOutboxWriter(outboxes), kafkaOptions, timeProvider);
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
            NullLogger<InventoryAllocationResultHandler>.Instance, eventProducer, null);

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
    public async Task PendingPayment_PaymentPastCutoff_AdvancesVersion_AndPayloadMatchesPersisted()
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

            long versionBefore;
            DateTimeOffset pastCutoffTime;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, itemId, supplierId);
                order.AttachInvoice("INV-VER-01", FixedNow);
                versionBefore = order.Version; // version = 2 after attach invoice
                pastCutoffTime = order.PaymentDeadline.AddHours(1); // well past 15 min cutoff
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Dispatch payment arriving past cutoff
            Guid inboxId = Guid.NewGuid();
            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(paidDb, now: pastCutoffTime);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-VER-01", "PAY-VER-01",
                    9_000_000m, "VND", pastCutoffTime);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), pastCutoffTime, "corr-ver-1", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    inboxId, env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-ver-1", null, envJson, pastCutoffTime, "fp-ver-1");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: pastCutoffTime), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess);

                // Notification must have incremented version
                Assert.Single(notifier.RenterNotifications);
                var note = notifier.RenterNotifications.Single();
                Assert.Equal(OrderChangeTypes.PaymentRefundRequired, note.Notification.ChangeType);
                Assert.True(note.Notification.Version > versionBefore,
                    $"Notification version {note.Notification.Version} should be > versionBefore {versionBefore}");
            }

            // Verify in new DbContext: DB, Outbox and SignalR are 100% consistent!
            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await verifyDb.RentalOrders.SingleAsync(o => o.Id == orderId);
                Assert.True(order.Version > versionBefore, $"DB version {order.Version} should be > versionBefore {versionBefore}");
                Assert.True(order.LatePaymentRefundRequired);

                // Outbox payload version matches persisted DB version
                var outboxMsg = await verifyDb.RentalOutboxMessages
                    .SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested);

                using var doc = JsonDocument.Parse(outboxMsg.Payload);
                long outboxVersion = doc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64();
                Assert.Equal(order.Version, outboxVersion);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CancelledOrder_PaymentArrives_AdvancesVersion_AndPayloadMatchesPersisted()
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

            long versionBefore;
            DateTimeOffset paymentTime = FixedNow.AddMinutes(5);
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, itemId, supplierId);
                order.AttachInvoice("INV-VER-CANC", FixedNow);
                order.MarkCancelled(FixedNow.AddMinutes(2), "op-cancel", "Renter requested cancellation");
                versionBefore = order.Version;
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Dispatch payment arriving on cancelled order
            Guid inboxId = Guid.NewGuid();
            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(paidDb, now: paymentTime);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-VER-CANC", "PAY-VER-CANC",
                    9_000_000m, "VND", paymentTime);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), paymentTime, "corr-ver-canc", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    inboxId, env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-ver-canc", null, envJson, paymentTime, "fp-ver-canc");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: paymentTime), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess);

                Assert.Single(notifier.RenterNotifications);
                var note = notifier.RenterNotifications.Single();
                Assert.Equal(OrderChangeTypes.PaymentRefundRequired, note.Notification.ChangeType);
                Assert.True(note.Notification.Version > versionBefore);
            }

            // Verify in new DbContext
            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await verifyDb.RentalOrders.SingleAsync(o => o.Id == orderId);
                Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
                Assert.True(order.LatePaymentRefundRequired);
                Assert.True(order.Version > versionBefore);

                var outboxMsg = await verifyDb.RentalOutboxMessages
                    .SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested);

                using var doc = JsonDocument.Parse(outboxMsg.Payload);
                long outboxVersion = doc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64();
                Assert.Equal(order.Version, outboxVersion);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ExpiredOrder_PaymentArrivesOutsideRecoveryWindow_AdvancesVersion()
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

            long versionBefore;
            DateTimeOffset paymentTime;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, itemId, supplierId);
                order.AttachInvoice("INV-VER-EXP", FixedNow);
                order.MarkPaymentExpired(FixedNow.AddMinutes(16), "op-expire");
                versionBefore = order.Version;
                paymentTime = order.PaymentDeadline.AddHours(2);
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Dispatch payment outside recovery window
            Guid inboxId = Guid.NewGuid();
            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(paidDb, now: paymentTime);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-VER-EXP", "PAY-VER-EXP",
                    9_000_000m, "VND", paymentTime);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), paymentTime, "corr-ver-exp", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    inboxId, env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-ver-exp", null, envJson, paymentTime, "fp-ver-exp");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: paymentTime), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inboxId);
                Assert.True(res.IsSuccess);

                Assert.Single(notifier.RenterNotifications);
                Assert.True(notifier.RenterNotifications.Single().Notification.Version > versionBefore);
            }

            // Verify in new DbContext
            await using (var verifyDb = new RentalServiceDbContext(dbOptions))
            {
                var order = await verifyDb.RentalOrders.SingleAsync(o => o.Id == orderId);
                Assert.Equal(RentalOrderStatus.Expired, order.Status);
                Assert.True(order.LatePaymentRefundRequired);
                Assert.True(order.Version > versionBefore);

                var outboxMsg = await verifyDb.RentalOutboxMessages
                    .SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested);

                using var doc = JsonDocument.Parse(outboxMsg.Payload);
                long outboxVersion = doc.RootElement.GetProperty("payload").GetProperty("orderVersion").GetInt64();
                Assert.Equal(order.Version, outboxVersion);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ReplaySameMessage_And_DifferentMessageIdSamePaymentRef_KeepsVersionUnchanged_NoEvents()
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

            DateTimeOffset pastCutoffTime;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, itemId, supplierId);
                order.AttachInvoice("INV-VER-REP", FixedNow);
                pastCutoffTime = order.PaymentDeadline.AddHours(1);
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // 1. Process payment first time
            Guid firstInboxId = Guid.NewGuid();
            Guid envelopeMessageId = Guid.NewGuid();
            string paymentRef = "PAY-VER-REP";
            long versionAfterFirstProcessing;

            var paidPayload = new InvoicePaidPayload(
                orderId, renterId, "INV-VER-REP", paymentRef,
                9_000_000m, "VND", pastCutoffTime);
            var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                envelopeMessageId, "InvoicePaid", 1, "payment-service",
                orderId.ToString("D"), pastCutoffTime, "corr-rep-1", null, paidPayload);
            string envJson = JsonSerializer.Serialize(env, JsonOptions);

            await using (var db1 = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(db1, now: pastCutoffTime);
                var inbox = new RentalInboxMessage(
                    firstInboxId, envelopeMessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-rep-1", null, envJson, pastCutoffTime, "fp-rep-1");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: pastCutoffTime), orderId.ToString("D"));
                db1.RentalInboxMessages.Add(inbox);
                await db1.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(firstInboxId);
                Assert.True(res.IsSuccess);
                Assert.Single(notifier.RenterNotifications);
            }

            await using (var verifyDb1 = new RentalServiceDbContext(dbOptions))
            {
                var o = await verifyDb1.RentalOrders.SingleAsync(x => x.Id == orderId);
                versionAfterFirstProcessing = o.Version;
            }

            // 2. Replay EXACT same inbox message -> No-op
            await using (var db2 = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(db2, now: pastCutoffTime.AddMinutes(1));
                var res = await dispatcher.ProcessInboxMessageAsync(firstInboxId);
                Assert.True(res.IsSuccess);
                Assert.Empty(notifier.RenterNotifications); // NO duplicate notifications!
            }

            // 3. New message with DIFFERENT messageId but SAME paymentRef (payment replay)
            Guid secondInboxId = Guid.NewGuid();
            Guid secondEnvelopeId = Guid.NewGuid();
            var secondEnv = new KafkaMessageEnvelope<InvoicePaidPayload>(
                secondEnvelopeId, "InvoicePaid", 1, "payment-service",
                orderId.ToString("D"), pastCutoffTime.AddMinutes(2), "corr-rep-2", null, paidPayload);
            string secondEnvJson = JsonSerializer.Serialize(secondEnv, JsonOptions);

            await using (var db3 = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(db3, now: pastCutoffTime.AddMinutes(2));
                var inbox2 = new RentalInboxMessage(
                    secondInboxId, secondEnvelopeId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-rep-2", null, secondEnvJson, pastCutoffTime.AddMinutes(2), "fp-rep-2");
                inbox2.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(secondEnvJson, issuedAt: pastCutoffTime.AddMinutes(2)), orderId.ToString("D"));
                db3.RentalInboxMessages.Add(inbox2);
                await db3.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(secondInboxId);
                Assert.True(res.IsSuccess);
                Assert.Empty(notifier.RenterNotifications); // NO duplicate notifications!
            }

            // 4. Verify version is STILL versionAfterFirstProcessing and outbox count has NOT increased
            await using (var finalDb = new RentalServiceDbContext(dbOptions))
            {
                var finalOrder = await finalDb.RentalOrders.SingleAsync(x => x.Id == orderId);
                Assert.Equal(versionAfterFirstProcessing, finalOrder.Version); // Version strictly unchanged!

                var outboxCount = await finalDb.RentalOutboxMessages.CountAsync(m => m.MessageType == KafkaContractSpec.EventTypes.LatePaymentRefundRequested);
                Assert.Equal(1, outboxCount); // Exactly 1 outbox message from initial processing!
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task CacheSimulation_ClientHoldsPreMutationVersion_EventTriggersRefetch()
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

            long cachedVersion;
            DateTimeOffset pastCutoffTime;
            await using (var seedDb = new RentalServiceDbContext(dbOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var order = CreateOrder(orderId, renterId, itemId, supplierId);
                order.AttachInvoice("INV-VER-CACHE", FixedNow);
                cachedVersion = order.Version; // UI cache holds this version (e.g. 2)
                pastCutoffTime = order.PaymentDeadline.AddHours(1);
                seedDb.RentalOrders.Add(order);
                await seedDb.SaveChangesAsync();
            }

            // Simulate UI client cache state
            bool refetchTriggered = false;
            long? refetchedVersion = null;

            await using (var paidDb = new RentalServiceDbContext(dbOptions))
            {
                var (dispatcher, notifier, _) = CreateDispatcher(paidDb, now: pastCutoffTime);
                var paidPayload = new InvoicePaidPayload(
                    orderId, renterId, "INV-VER-CACHE", "PAY-VER-CACHE",
                    9_000_000m, "VND", pastCutoffTime);

                var env = new KafkaMessageEnvelope<InvoicePaidPayload>(
                    Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                    orderId.ToString("D"), pastCutoffTime, "corr-cache-1", null, paidPayload);
                string envJson = JsonSerializer.Serialize(env, JsonOptions);

                var inbox = new RentalInboxMessage(
                    Guid.NewGuid(), env.MessageId, "InvoicePaid", "payment-service",
                    orderId.ToString("D"), "corr-cache-1", null, envJson, pastCutoffTime, "fp-cache-1");
                inbox.AssignPublicationProof(PaymentPublicationProofTestFixture.Create(envJson, issuedAt: pastCutoffTime), orderId.ToString("D"));

                paidDb.RentalInboxMessages.Add(inbox);
                await paidDb.SaveChangesAsync();

                var res = await dispatcher.ProcessInboxMessageAsync(inbox.Id);
                Assert.True(res.IsSuccess);

                // Receive event on client
                var notification = notifier.RenterNotifications.Single().Notification;

                // Client cache check:
                if (notification.Version > cachedVersion)
                {
                    refetchTriggered = true;
                    // Simulate client calling REST GET /api/v1/rental/orders/{orderId}
                    var repo = new RentalOrderRepository(paidDb);
                    var reloaded = await repo.GetByIdWithDetailsAsync(orderId);
                    refetchedVersion = reloaded!.Version;
                }
            }

            // Assert client was NOT fooled by stale version!
            Assert.True(refetchTriggered, "Client MUST trigger refetch because event version > cached version");
            Assert.NotNull(refetchedVersion);
            Assert.True(refetchedVersion.Value > cachedVersion);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
