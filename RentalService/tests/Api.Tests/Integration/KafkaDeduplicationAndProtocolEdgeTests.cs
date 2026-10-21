using System.Text.Json;
using Confluent.Kafka;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaDeduplicationAndProtocolEdgeTests
{
    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    [Fact]
    public void CanonicalJson_NormalizesPropertyOrder_PreservesArrayOrder()
    {
        string json1 = """
        {
            "b": 2,
            "a": 1,
            "nested": { "z": 9, "m": 5 },
            "arr": [3, 1, 2]
        }
        """;

        string json2 = """
        {
            "a": 1,
            "nested": { "m": 5, "z": 9 },
            "b": 2,
            "arr": [3, 1, 2]
        }
        """;

        string jsonArrDifferentOrder = """
        {
            "a": 1,
            "nested": { "m": 5, "z": 9 },
            "b": 2,
            "arr": [1, 2, 3]
        }
        """;

        Assert.True(CanonicalJson.AreEquivalent(json1, json2));
        Assert.False(CanonicalJson.AreEquivalent(json1, jsonArrDifferentOrder));

        string norm1 = CanonicalJson.Normalize(json1);
        string norm2 = CanonicalJson.Normalize(json2);
        Assert.Equal(norm1, norm2);
    }

    [Fact]
    public void CanonicalJson_DetectsNestedPayloadChanges_AndTypeMismatches()
    {
        string original = """{"orderId":"123","amount":100}""";
        string changedValue = """{"orderId":"123","amount":101}""";
        string changedType = """{"orderId":"123","amount":"100"}""";

        Assert.False(CanonicalJson.AreEquivalent(original, changedValue));
        Assert.False(CanonicalJson.AreEquivalent(original, changedType));
    }

    [Fact]
    public async Task Dispatcher_ReorderedJson_RecognizedAsDuplicate()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        var dispatcher = new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            new StubPaymentWorkflow(),
            new StubRefundConsumer(),
            new OutboxRepository(db),
            db,
            Options.Create(Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions { PaymentEventsTopic = "payment.events.v1" })));

        Guid messageId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        string json1 = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-14T08:00:00Z",
            "correlationId": "corr-1",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{renterId}}",
                "invoiceRef": "INV-001",
                "paymentRef": "PAY-001",
                "paidAmount": 1000000,
                "currency": "VND",
                "paidAt": "2026-10-14T08:00:00Z"
            }
        }
        """;

        // Same content with reversed property order
        string json2 = $$"""
        {
            "payload": {
                "paidAt": "2026-10-14T08:00:00Z",
                "currency": "VND",
                "paidAmount": 1000000,
                "paymentRef": "PAY-001",
                "invoiceRef": "INV-001",
                "renterId": "{{renterId}}",
                "rentalOrderId": "{{orderId}}"
            },
            "correlationId": "corr-1",
            "occurredAt": "2026-10-14T08:00:00Z",
            "aggregateId": "{{orderId}}",
            "producer": "payment-service",
            "version": 1,
            "messageType": "InvoicePaid",
            "messageId": "{{messageId}}"
        }
        """;

        var res1 = await dispatcher.ReceiveMessageAsync(json1,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(json1));
        Assert.True(res1.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.InboxStored, res1.Value);

        // Deduplication using canonical JSON: matches existing inbox payload even when reversed
        var res2 = await dispatcher.ReceiveMessageAsync(json2,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(json2));
        Assert.True(res2.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.Duplicate, res2.Value);

        int count = await db.RentalInboxMessages.CountAsync(m => m.MessageId == messageId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Dispatcher_AlteredNestedPayload_RoutedToDlqAsConflict()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        var dispatcher = new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            new StubPaymentWorkflow(),
            new StubRefundConsumer(),
            new OutboxRepository(db),
            db,
            Options.Create(Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions { PaymentEventsTopic = "payment.events.v1", PaymentDlqTopic = "payment.events.v1.dlq" })));

        Guid messageId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        string jsonOriginal = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-14T08:00:00Z",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-001",
                "paymentRef": "PAY-001",
                "paidAmount": 1000000,
                "currency": "VND",
                "paidAt": "2026-10-14T08:00:00Z"
            }
        }
        """;

        string jsonConflicting = $$"""
        {
            "messageId": "{{messageId}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-14T08:00:00Z",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-001",
                "paymentRef": "PAY-001",
                "paidAmount": 9999999,
                "currency": "VND",
                "paidAt": "2026-10-14T08:00:00Z"
            }
        }
        """;

        var res1 = await dispatcher.ReceiveMessageAsync(jsonOriginal,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(jsonOriginal));
        Assert.True(res1.IsSuccess);

        var res2 = await dispatcher.ReceiveMessageAsync(jsonConflicting,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(jsonConflicting));
        Assert.True(res2.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res2.Value);
        // Verify routed to DLQ Outbox
        var dlqMsg = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "DeadLetter");
        Assert.NotNull(dlqMsg);
        Assert.Equal("payment.events.v1.dlq", dlqMsg.Topic);
    }

    [Fact]
    public async Task ProcessInboxMessage_UnderOrderLock_ConcurrentContention_WorkflowRunsExactlyOnce()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var initDb = CreateDbContext(dbName);

        Guid orderId = Guid.NewGuid();
        Guid msgId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset fixedNow = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

        var order = RentalOrder.Create(
            orderId, "ORD-CONC-01", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-CONC", fixedNow,
            new[] { new InitialRentalItemDto(itemId, Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-CONC", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        initDb.RentalOrders.Add(order);

        var payload = new InvoicePaidPayload(orderId, order.RenterId, "INV-CONC", "PAY-CONC", order.TotalPayableAmount, "VND", fixedNow);
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            msgId, "InvoicePaid", 1, "payment-service", orderId.ToString("D"), fixedNow, "corr", null, payload);

        string inboxPayload = JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(),
            msgId,
            "InvoicePaid",
            "payment-service",
            orderId.ToString("D"),
            null,
            null,
            inboxPayload,
            fixedNow);
        inboxMsg.AssignPublicationProof(
            Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(inboxPayload),
            orderId.ToString("D"));

        initDb.RentalInboxMessages.Add(inboxMsg);
        await initDb.SaveChangesAsync();
        var proofValidation = Dprental.RentalService.Application.Kafka.Consumers.PaymentPublicationProofVerifier.Verify(
            inboxMsg.Payload, inboxMsg.PublicationProofJson, null, orderId.ToString("D"),
            Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions()), DateTimeOffset.UtcNow);
        Assert.True(proofValidation.IsSuccess, proofValidation.Error.Message);

        int executionCount = 0;
        var workflow = new StubPaymentWorkflow
        {
            Handler = async _ =>
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(50);
                return Result.Success();
            }
        };

        var lockManager = new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager();

        await using var db1 = CreateDbContext(dbName);
        var dispatcher1 = new PaymentEventsDispatcher(
            new InboxRepository(db1),
            new RentalOrderRepository(db1),
            lockManager,
            workflow,
            new StubRefundConsumer(),
            new OutboxRepository(db1),
            db1,
            Options.Create(Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions())));

        await using var db2 = CreateDbContext(dbName);
        var dispatcher2 = new PaymentEventsDispatcher(
            new InboxRepository(db2),
            new RentalOrderRepository(db2),
            lockManager,
            workflow,
            new StubRefundConsumer(),
            new OutboxRepository(db2),
            db2,
            Options.Create(Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions())));

        var task1 = Task.Run(() => dispatcher1.ProcessInboxMessageAsync(inboxMsg.Id));
        var task2 = Task.Run(() => dispatcher2.ProcessInboxMessageAsync(inboxMsg.Id));

        var results = await Task.WhenAll(task1, task2);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, executionCount);

        await using var verifyDb = CreateDbContext(dbName);
        var finalMsg = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxMsg.Id);
        Assert.Equal("Processed", finalMsg.Status);
    }

    [Fact]
    public async Task DLQ_Redelivery_DoesNotCreateAdditionalOutboxRecord()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        var dispatcher = new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            new StubPaymentWorkflow(),
            new StubRefundConsumer(),
            new OutboxRepository(db),
            db,
            Options.Create(new KafkaOptions { PaymentEventsTopic = "payment.events.v1", PaymentDlqTopic = "payment.events.v1.dlq" }));

        string malformed = "{ this is malformed json }";

        // First delivery: routes to DLQ
        var res1 = await dispatcher.ReceiveMessageAsync(malformed, topic: "payment.events.v1", partition: 0, offset: 100);
        Assert.True(res1.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res1.Value);

        int dlqCount1 = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(1, dlqCount1);

        // Redelivery of exact same message at same coordinates: recognized as DLQ replay, does not create duplicate
        var res2 = await dispatcher.ReceiveMessageAsync(malformed, topic: "payment.events.v1", partition: 0, offset: 100);
        Assert.True(res2.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res2.Value);

        int dlqCount2 = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(1, dlqCount2);
    }

    [Fact]
    public async Task DLQ_DifferentInvalidRecords_WithSameOriginalMessageId_AreNotMerged()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        var dispatcher = new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            new StubPaymentWorkflow(),
            new StubRefundConsumer(),
            new OutboxRepository(db),
            db,
            Options.Create(new KafkaOptions { PaymentEventsTopic = "payment.events.v1", PaymentDlqTopic = "payment.events.v1.dlq" }));

        Guid originalMessageId = Guid.NewGuid();

        Guid aggId1 = Guid.NewGuid();
        Guid aggId2 = Guid.NewGuid();
        string msg1 = $"{{\"messageId\":\"{originalMessageId}\",\"version\":999,\"messageType\":\"InvoicePaid\",\"producer\":\"payment-service\",\"aggregateId\":\"{aggId1}\",\"payload\":{{}}}}";
        string msg2 = $"{{\"messageId\":\"{originalMessageId}\",\"version\":888,\"messageType\":\"InvoicePaid\",\"producer\":\"payment-service\",\"aggregateId\":\"{aggId2}\",\"payload\":{{}}}}";
        var res1 = await dispatcher.ReceiveMessageAsync(msg1, topic: "payment.events.v1", partition: 0, offset: 200);
        Assert.True(res1.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res1.Value);

        var res2 = await dispatcher.ReceiveMessageAsync(msg2, topic: "payment.events.v1", partition: 0, offset: 201);
        Assert.True(res2.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, res2.Value);

        // Both invalid records must exist as distinct DLQ records; not merged!
        int dlqCount = await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "DeadLetter");
        Assert.Equal(2, dlqCount);
    }

    [Fact]
    public async Task OutboxPublisher_PrePublishLeaseCheck_SkipsExpiredLease()
    {
        var expiredMsg = new RentalOutboxMessage(
            Guid.NewGuid(),
            "rental.events.v1",
            "key-1",
            "TestEvent",
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        // Explicitly set lock in the past to simulate lease expiration between claim and publish
        expiredMsg.Claim("worker-1", DateTimeOffset.UtcNow.AddSeconds(-5));

        var stubOutboxRepo = new StubClaimOutboxRepository(expiredMsg);
        var fakeTransport = new FakeProducerTransport();

        var services = new ServiceCollection();
        services.AddScoped<IOutboxRepository>(_ => stubOutboxRepo);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var publisher = new OutboxPublisherWorker(
            scopeFactory,
            Options.Create(new KafkaOptions { Enabled = true, MaxBatchPerRound = 1, DeliveryTimeoutSeconds = 1 }),
            NullLogger<OutboxPublisherWorker>.Instance,
            fakeTransport);

        int published = await publisher.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, published);
        Assert.Equal(0, fakeTransport.ProduceCallCount);
    }

    private sealed class StubClaimOutboxRepository : IOutboxRepository
    {
        private readonly RentalOutboxMessage _message;
        private bool _claimed;

        public StubClaimOutboxRepository(RentalOutboxMessage message) => _message = message;

        public Task<RentalOutboxMessage?> ClaimNextPendingAsync(string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            if (!_claimed)
            {
                _claimed = true;
                return Task.FromResult<RentalOutboxMessage?>(_message);
            }
            return Task.FromResult<RentalOutboxMessage?>(null);
        }


        public Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new OutboxEnqueueResult(message, false)));

        public Task AddAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> MarkPublishedAsync(Guid id, string claimToken, DateTimeOffset publishedAt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RecordFailureAsync(Guid id, string claimToken, string errorMessage, DateTimeOffset attemptedAt, DateTimeOffset? nextScheduledAt = null, string? targetStatus = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubPaymentWorkflow : IPaymentWorkflowService
    {
        public Func<KafkaMessageEnvelope<InvoicePaidPayload>, Task<Result>>? Handler { get; set; }

        public async Task<Result<bool>> ApplyInvoicePaidStagedAsync(RentalOrder order, KafkaMessageEnvelope<InvoicePaidPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default)
        {
            if (Handler != null)
            {
                var res = await Handler(envelope);
                return res.IsFailure ? res.Error : Result.Success(true);
            }
            return Result.Success(true);
        }

        public Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default) =>
            Handler != null ? Handler(envelope) : Task.FromResult(Result.Success());
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public Task<Result<bool>> ApplyRefundCompletedStagedAsync(RentalOrder order, KafkaMessageEnvelope<RefundCompletedPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(true));

        public Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeProducerTransport : IKafkaProducerTransport
    {
        public int ProduceCallCount { get; private set; }

        public Task<DeliveryResult<string, string>> ProduceAsync(string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            ProduceCallCount++;
            return Task.FromResult(new DeliveryResult<string, string>
            {
                Topic = topic,
                Partition = 0,
                Offset = 0,
                Status = PersistenceStatus.Persisted
            });
        }

        public void Flush(TimeSpan timeout) { }
        public void Dispose() { }
    }
}
