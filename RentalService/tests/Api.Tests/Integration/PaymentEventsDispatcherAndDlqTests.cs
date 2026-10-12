using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Integration;
using Dprental.RentalService.Application.Payments;
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
        return new PaymentEventsDispatcher(
            new InboxRepository(db),
            new RentalOrderRepository(db),
            new Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager(),
            paymentWorkflow,
            refundConsumer,
            new OutboxRepository(db),
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<PaymentEventsDispatcher>.Instance);
    }

    private sealed class StubPaymentWorkflowService : IPaymentWorkflowService
    {
        public bool ProcessCalled { get; set; }
        public Task<Result> ApplyInvoicePaidStagedAsync(
            RentalOrder order,
            PaymentEventEnvelope<InvoicePaidPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ProcessInvoicePaidAsync(PaymentEventEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public bool ProcessCalled { get; set; }
        public Task<Result> ApplyRefundCompletedStagedAsync(
            RentalOrder order,
            PaymentEventEnvelope<RefundCompletedPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ProcessRefundCompletedAsync(PaymentEventEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default)
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
        var result = await dispatcher.DispatchRawMessageAsync(malformed);

        Assert.True(result.IsFailure);
        Assert.Equal("MALFORMED_JSON", result.Error.Code);

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
        var result = await dispatcher.DispatchRawMessageAsync(noType);

        Assert.True(result.IsFailure);
        Assert.Equal("MISSING_MESSAGE_TYPE", result.Error.Code);

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

        var result = await dispatcher.DispatchRawMessageAsync(validJson);

        Assert.True(result.IsSuccess);
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

        var result = await dispatcher.DispatchRawMessageAsync(validJson);

        Assert.True(result.IsSuccess);
        Assert.True(refundConsumer.ProcessCalled);
    }
}
