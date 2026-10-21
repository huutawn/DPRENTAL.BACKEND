using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Integration;

using Microsoft.Extensions.Options;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
public sealed class PaymentEventsDispatcherAndDlqTests
{
    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static PaymentEventsDispatcher CreateDispatcher(
        RentalServiceDbContext db,
        IPaymentWorkflowService paymentWorkflow,
        IRefundCompletedConsumer refundConsumer)
    {
        var outboxRepo = new OutboxRepository(db);
        var options = Options.Create(Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Configure(new KafkaOptions
        {
            PaymentEventsTopic = "payment.events.v1",
            PaymentDlqTopic = "payment.events.v1.dlq"
        }));
        return new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            paymentWorkflow,
            refundConsumer,
            new KafkaOutboxWriter(outboxRepo),
            db,
            options,
            NullLogger<PaymentEventsDispatcher>.Instance);
    }
    private sealed class StubPaymentWorkflowService : IPaymentWorkflowService
    {
        public bool ProcessCalled { get; set; }
        public Task<Result<bool>> ApplyInvoicePaidStagedAsync(
            RentalOrder order,
            KafkaMessageEnvelope<InvoicePaidPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success(true));
        }

        public Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public bool ProcessCalled { get; set; }
        public Task<Result<bool>> ApplyRefundCompletedStagedAsync(
            RentalOrder order,
            KafkaMessageEnvelope<RefundCompletedPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success(true));
        }

        public Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }
    }
    [Fact]
    public async Task Dispatcher_MalformedJson_RoutesToDlqViaOutbox()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var paymentWorkflow = new StubPaymentWorkflowService();
        var refundConsumer = new StubRefundConsumer();
        var dispatcher = CreateDispatcher(db, paymentWorkflow, refundConsumer);
        string malformed = "{ this is not json }";
        var result = await dispatcher.ReceiveMessageAsync(malformed);

        Assert.True(result.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, result.Value);

        var dlqOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "DeadLetter");
        Assert.NotNull(dlqOutbox);
        Assert.Equal("payment.events.v1.dlq", dlqOutbox.Topic);
    }

    [Fact]
    public async Task Dispatcher_MissingMessageType_RoutesToDlqViaOutbox()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var paymentWorkflow = new StubPaymentWorkflowService();
        var refundConsumer = new StubRefundConsumer();
        var dispatcher = CreateDispatcher(db, paymentWorkflow, refundConsumer);
        string noType = """{"producer":"payment-service","payload":{}}""";
        var result = await dispatcher.ReceiveMessageAsync(noType);

        Assert.True(result.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.DlqStored, result.Value);

        var dlqOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "DeadLetter");
        Assert.NotNull(dlqOutbox);
    }

    [Fact]
    public async Task Dispatcher_ValidInvoicePaid_RoutesToPaymentWorkflowService()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var paymentWorkflow = new StubPaymentWorkflowService();
        var refundConsumer = new StubRefundConsumer();
        var dispatcher = CreateDispatcher(db, paymentWorkflow, refundConsumer);

        Guid orderId = Guid.NewGuid();
        var order = RentalOrder.Create(
            orderId, "ORD-101", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-101", DateTimeOffset.UtcNow,
            new[] { new InitialRentalItemDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-101", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        string validJson = $$"""
        {
            "messageId": "{{Guid.NewGuid()}}",
            "messageType": "InvoicePaid",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-08T08:15:30Z",
            "correlationId": "corr-1",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "renterId": "{{Guid.NewGuid()}}",
                "invoiceRef": "INV-001",
                "paymentRef": "PAY-001",
                "paidAmount": 1500000,
                "currency": "VND",
                "paidAt": "2026-10-08T08:15:30Z"
            }
        }
        """;
        var intakeResult = await dispatcher.ReceiveMessageAsync(validJson,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(validJson));
        Assert.True(intakeResult.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.InboxStored, intakeResult.Value);

        var inboxMsg = await db.RentalInboxMessages.FirstAsync(m => m.AggregateId == orderId.ToString("D"));
        var result = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.True(paymentWorkflow.ProcessCalled);
    }

    [Fact]
    public async Task Dispatcher_ValidRefundCompleted_RoutesToRefundConsumer()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var paymentWorkflow = new StubPaymentWorkflowService();
        var refundConsumer = new StubRefundConsumer();
        var dispatcher = CreateDispatcher(db, paymentWorkflow, refundConsumer);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var order = RentalOrder.Create(
            orderId, "ORD-102", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-102", DateTimeOffset.UtcNow,
            new[] { new InitialRentalItemDto(itemId, Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-102", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        string validJson = $$"""
        {
            "messageId": "{{Guid.NewGuid()}}",
            "messageType": "RefundCompleted",
            "version": 1,
            "producer": "payment-service",
            "aggregateId": "{{orderId}}",
            "occurredAt": "2026-10-08T08:15:30Z",
            "correlationId": "corr-2",
            "payload": {
                "rentalOrderId": "{{orderId}}",
                "invoiceRef": "INV-001",
                "paymentRef": "PAY-001",
                "refundRef": "REF-001",
                "operationKey": "op-ref-1",
                "totalRefundedAmount": 1500000,
                "currency": "VND",
                "items": [
                    {
                        "rentalItemId": "{{itemId}}",
                        "refundedRentalAmount": 500000,
                        "refundedDepositAmount": 1000000
                    }
                ],
                "completedAt": "2026-10-08T08:15:30Z"
            }
        }
        """;
        var intakeResult = await dispatcher.ReceiveMessageAsync(validJson,
            publicationProof: Dprental.RentalService.Api.Tests.TestHelpers.PaymentPublicationProofTestFixture.Create(validJson));
        Assert.True(intakeResult.IsSuccess);
        Assert.Equal(KafkaIntakeDisposition.InboxStored, intakeResult.Value);

        var inboxMsg = await db.RentalInboxMessages.FirstAsync(m => m.AggregateId == orderId.ToString("D"));
        var result = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.True(refundConsumer.ProcessCalled);
    }

    [Fact]
    public async Task ProcessInboxMessage_WhenDlqRouteFails_DoesNotMarkInboxAsDeadLettered()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        Guid orderId = Guid.NewGuid();
        Guid msgId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var order = RentalOrder.Create(
            orderId, "ORD-DLQ-FAIL", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-DLQ-FAIL", DateTimeOffset.UtcNow,
            new[] { new InitialRentalItemDto(itemId, Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-DLQ", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        db.RentalOrders.Add(order);
        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(),
            msgId,
            "InvoicePaid",
            "payment-service",
            orderId.ToString("D"),
            null,
            null,
            "{ invalid json payload }",
            DateTimeOffset.UtcNow);

        db.RentalInboxMessages.Add(inboxMsg);
        await db.SaveChangesAsync();

        var failingWriter = new FailingKafkaOutboxWriter();

        var dispatcher = new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            new StubPaymentWorkflowService(),
            new StubRefundConsumer(),
            failingWriter,
            db,
            Options.Create(new KafkaOptions { PaymentEventsTopic = "payment.events.v1", PaymentDlqTopic = "payment.events.v1.dlq" }),
            NullLogger<PaymentEventsDispatcher>.Instance);

        var result = await dispatcher.ProcessInboxMessageAsync(inboxMsg.Id);
        Assert.True(result.IsFailure);

        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxMsg.Id);
        Assert.Equal(RentalInboxMessage.StatusReceived, reloaded.Status);
    }

    private sealed class FailingKafkaOutboxWriter : IKafkaOutboxWriter
    {
        public Task<Result<ProducerEnqueueResult>> EnqueueAsync<TPayload>(string topic, string partitionKey, KafkaMessageEnvelope<TPayload> candidateEnvelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure<ProducerEnqueueResult>(Error.Failure("OUTBOX_PERSISTENCE_FAILED", "Simulated DLQ persistence failure.")));
        }
    }
}
