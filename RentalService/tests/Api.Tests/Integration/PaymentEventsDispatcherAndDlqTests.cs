using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Integration;
using Dprental.RentalService.Application.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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

    private sealed class StubPaymentWorkflowService : IPaymentWorkflowService
    {
        public bool ProcessCalled { get; set; }
        public Task<Result> ProcessInvoicePaidAsync(PaymentEventEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default)
        {
            ProcessCalled = true;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public bool ProcessCalled { get; set; }
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
        var dispatcher = new PaymentEventsDispatcher(paymentWorkflow, refundConsumer, new OutboxRepository(db), db, new ConfigurationBuilder().Build(), NullLogger<PaymentEventsDispatcher>.Instance);

        string malformed = "{ this is not json }";
        var result = await dispatcher.DispatchRawMessageAsync(malformed);

        Assert.True(result.IsFailure);
        Assert.Equal("MALFORMED_JSON", result.Error.Code);

        var dlqOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "DeadLetter");
        Assert.NotNull(dlqOutbox);
        Assert.Equal("dlq", dlqOutbox.PartitionKey);
    }

    [Fact]
    public async Task Dispatcher_MissingMessageType_RoutesToDlqViaOutbox()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var paymentWorkflow = new StubPaymentWorkflowService();
        var refundConsumer = new StubRefundConsumer();
        var dispatcher = new PaymentEventsDispatcher(paymentWorkflow, refundConsumer, new OutboxRepository(db), db, new ConfigurationBuilder().Build(), NullLogger<PaymentEventsDispatcher>.Instance);

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
        var dispatcher = new PaymentEventsDispatcher(paymentWorkflow, refundConsumer, new OutboxRepository(db), db, new ConfigurationBuilder().Build(), NullLogger<PaymentEventsDispatcher>.Instance);

        Guid orderId = Guid.NewGuid();
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
        var dispatcher = new PaymentEventsDispatcher(paymentWorkflow, refundConsumer, new OutboxRepository(db), db, new ConfigurationBuilder().Build(), NullLogger<PaymentEventsDispatcher>.Instance);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
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
