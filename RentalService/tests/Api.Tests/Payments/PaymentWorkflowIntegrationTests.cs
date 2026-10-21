using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Payments;

public sealed class PaymentWorkflowIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateOrder(
        Guid orderId,
        Guid renterId,
        decimal dailyRate = 200_000m,
        decimal depositPerUnit = 500_000m,
        int quantity = 2,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        string? invoiceRef = "INV-001")
    {
        var start = startDate ?? new DateOnly(2026, 10, 15);
        var end = endDate ?? new DateOnly(2026, 10, 18);
        var assignments = Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var item = new InitialRentalItemDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            quantity,
            start,
            end,
            "Camera Sony",
            null,
            "Pickup",
            dailyRate,
            depositPerUnit,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-PAY-001",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [item]).Value;

        if (invoiceRef != null)
        {
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))?.SetValue(order, invoiceRef);
        }

        return order;
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ExtendHoldRequest, Task<Result<ExtendHoldResponse>>>? ExtendHoldHandler { get; set; }
        public Func<string, ReleaseAllocationRequest, Task<Result<ReleaseAllocationResponse>>>? ReleaseHandler { get; set; }

        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) =>
            ExtendHoldHandler != null
                ? ExtendHoldHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ExtendHoldResponse("REC-001", allocationGroupId, request.NewHoldExpiresAt, FixedNow, false)));

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            ReleaseHandler != null
                ? ReleaseHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Func<RequestRefundRequest, Task<Result<RequestRefundResponse>>>? RequestRefundHandler { get; set; }

        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            RequestRefundHandler != null
                ? RequestRefundHandler(request)
                : Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static PaymentWorkflowService CreateWorkflowService(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        DateTimeOffset? now = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var inboxRepo = new InboxRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(now ?? FixedNow);
        var config = new ConfigurationBuilder().Build();
        var executor = new RentalOperationExecutor(orderRepo, receiptRepo, outboxRepo, lockManager, inventoryClient, paymentClient, db, config, NullLogger<RentalOperationExecutor>.Instance, time);
        var options = Options.Create(new KafkaOptions());
        return new PaymentWorkflowService(orderRepo, inboxRepo, outboxRepo, receiptRepo, lockManager,
            executor, db, new InventoryCommandProducer(new KafkaOutboxWriter(outboxRepo), options, time),
            config, NullLogger<PaymentWorkflowService>.Instance,
            new RentalEventProducer(new KafkaOutboxWriter(outboxRepo), options, time), time);
    }

    [Fact]
    public async Task ProcessInvoicePaid_ValidPayment_MovesOrderToPaid_AndEmitsSupplierConfirmationRequested()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId, dailyRate: 200_000m, depositPerUnit: 500_000m, quantity: 2);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient();
        var paymentClient = new StubPaymentClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-001", order.TotalPayableAmount, "VND", FixedNow));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);

        // Verify Order updated
        var reloadedOrder = await db.RentalOrders.Include(o => o.Items).FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Paid, reloadedOrder.Status);
        Assert.Equal("PAY-001", reloadedOrder.PaymentRef);
        Assert.Equal("INV-001", reloadedOrder.InvoiceRef);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, reloadedOrder.Items.First().Status);

        // Verify Outbox message emitted
        var outboxMessages = await db.RentalOutboxMessages.ToListAsync();
        Assert.Contains(outboxMessages, m => m.MessageType == "ExtendHold");

        // Verify Inbox message recorded
        var inboxMessage = await db.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == envelope.MessageId);
        Assert.NotNull(inboxMessage);
        Assert.Equal("Processed", inboxMessage.Status);
    }

    [Fact]
    public async Task ProcessInvoicePaid_ExtendsHoldFiveMinutesPastTheSupplierDeadline()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId, dailyRate: 200_000m, depositPerUnit: 500_000m, quantity: 2);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        DateTimeOffset? requestedHoldExpiry = null;
        var inventoryClient = new StubInventoryClient
        {
            ExtendHoldHandler = (groupId, req) =>
            {
                requestedHoldExpiry = req.NewHoldExpiresAt;
                return Task.FromResult(Result.Success(new ExtendHoldResponse("REC-001", groupId, req.NewHoldExpiresAt, FixedNow, false)));
            }
        };
        var service = CreateWorkflowService(db, inventoryClient, new StubPaymentClient());

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-hold",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-001", order.TotalPayableAmount, "VND", FixedNow));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);
        var item = (await db.RentalOrders.Include(o => o.Items).FirstAsync(o => o.Id == orderId)).Items.First();
        // Supplier vẫn phải quyết định trước hạn; hold còn thêm 5 phút để worker chốt nốt đơn con đã bấm nhận trước hạn.
        Assert.Equal(FixedNow.AddHours(24), item.SupplierConfirmationDeadline);
        var extendOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "ExtendHold");
        Assert.NotNull(extendOutbox);
        var extendEnvelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<ExtendHoldCommandPayload>>(extendOutbox.Payload, JsonOptions);
        Assert.Equal(FixedNow.AddHours(24).AddMinutes(5), extendEnvelope!.Payload.NewHoldExpiresAt);
    }

    [Fact]
    public async Task ProcessInvoicePaid_ReplayedMessage_ReturnsSuccessWithoutDuplication()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient();
        var paymentClient = new StubPaymentClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-001", order.TotalPayableAmount, "VND", FixedNow));

        var first = await service.ProcessInvoicePaidAsync(envelope);
        Assert.True(first.IsSuccess);

        int outboxCountAfterFirst = await db.RentalOutboxMessages.CountAsync();

        // Replay with exact same envelope
        var second = await service.ProcessInvoicePaidAsync(envelope);
        Assert.True(second.IsSuccess);

        // No new outbox messages
        int outboxCountAfterSecond = await db.RentalOutboxMessages.CountAsync();
        Assert.Equal(outboxCountAfterFirst, outboxCountAfterSecond);
    }

    [Fact]
    public async Task ProcessInvoicePaid_SnapshotMismatch_ReturnsValidationError()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient();
        var paymentClient = new StubPaymentClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient);

        // Wrong amount
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-001", order.TotalPayableAmount - 10_000m, "VND", FixedNow));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsFailure);
        Assert.Equal("AMOUNT_MISMATCH", result.Error.Code);
    }

    [Fact]
    public async Task ProcessInvoicePaid_LatePaymentOnExpiredOrder_Requests100PercentRefund_AndKeepsTerminalStatus()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        order.MarkPaymentExpired(FixedNow, "op-expire");
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        bool refundCalled = false;
        var paymentClient = new StubPaymentClient
        {
            RequestRefundHandler = req =>
            {
                refundCalled = true;
                Assert.Equal(order.TotalPayableAmount, req.TotalRefundAmount);
                return Task.FromResult(Result.Success(new RequestRefundResponse("REF-LATE-001", req.OperationKey, "Accepted", req.TotalRefundAmount, "VND", false)));
            }
        };

        var inventoryClient = new StubInventoryClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient, FixedNow.AddHours(2));

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow.AddHours(2),
            "corr-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-LATE", order.TotalPayableAmount, "VND", FixedNow.AddHours(2)));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);
        Assert.False(refundCalled);

        var reloaded = await db.RentalOrders.Include(o => o.OperationReceipts).FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
        Assert.True(reloaded.LatePaymentRefundRequired);
        Assert.Equal("PAY-LATE", reloaded.PaymentRef);

        var refundReceipt = reloaded.OperationReceipts.FirstOrDefault(r => r.Action == "RequestRefund");
        Assert.NotNull(refundReceipt);
        Assert.Equal(RentalOperationReceipt.StatusPending, refundReceipt.Status);
        Assert.False(refundReceipt.IsSuccess);

        var outbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "LatePaymentRefundRequested");
        Assert.NotNull(outbox);
    }
    [Fact]
    public async Task ProcessInvoicePaid_PaymentDispositionRequiresRefund_EvenWhenBankTimePrecedesDeadline()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId); // PendingPayment, deadline = FixedNow + 30m
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient();
        var paymentClient = new StubPaymentClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient, FixedNow.AddMinutes(35));

        // paidAt is before deadline (FixedNow + 10m < FixedNow + 30m), but processing occurredAt and consume time are after deadline (FixedNow + 35m)
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow.AddMinutes(35),
            "corr-2",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-LATE-PROC", order.TotalPayableAmount, "VND", FixedNow.AddMinutes(10),
                "LATE_REQUIRES_REFUND", FixedNow.AddMinutes(35)));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);

        var reloaded = await db.RentalOrders.Include(o => o.OperationReceipts).FirstAsync(o => o.Id == orderId);
        // Payment's final disposition takes precedence over the bank timestamp.
        Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
        Assert.True(reloaded.LatePaymentRefundRequired);
        Assert.Equal("PAY-LATE-PROC", reloaded.PaymentRef);
        Assert.Equal(FixedNow.AddMinutes(10), reloaded.PaymentConfirmedAt);

        var refundReceipt = reloaded.OperationReceipts.FirstOrDefault(r => r.Action == "RequestRefund");
        Assert.NotNull(refundReceipt);
        Assert.Equal(RentalOperationReceipt.StatusPending, refundReceipt.Status);

        var lateOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "LatePaymentRefundRequested");
        Assert.NotNull(lateOutbox);

        var extendOutbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "ExtendHold");
        Assert.Null(extendOutbox);
    }

    [Theory]
    [InlineData(-2000, 5000, true)]   // Deadline - 2s, OccurredAt = Deadline + 5s -> On-time
    [InlineData(-1, 10000, true)]     // Deadline - 1 tick (1ms/1tick earlier), OccurredAt = after -> On-time
    [InlineData(0, -5000, false)]     // Exactly deadline, OccurredAt = before -> Late
    [InlineData(0, 5000, false)]      // Exactly deadline, OccurredAt = after -> Late
    [InlineData(1000, -5000, false)]  // After deadline, OccurredAt = before -> Late
    public async Task ProcessInvoicePaid_DeadlineBoundaries_DeterminedStrictlyByPaidAt(
        long paidAtOffsetMs,
        long occurredAtOffsetMs,
        bool expectOnTime)
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId); // deadline = FixedNow + 30m
        DateTimeOffset deadline = order.PaymentDeadline;
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        DateTimeOffset paidAt = paidAtOffsetMs == -1 ? deadline.AddTicks(-1) : deadline.AddMilliseconds(paidAtOffsetMs);
        DateTimeOffset occurredAt = deadline.AddMilliseconds(occurredAtOffsetMs);

        var inventoryClient = new StubInventoryClient();
        var paymentClient = new StubPaymentClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient, occurredAt);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            occurredAt,
            "corr-boundary",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", $"PAY-{paidAtOffsetMs}-{occurredAtOffsetMs}", order.TotalPayableAmount, "VND", paidAt));

        var result = await service.ProcessInvoicePaidAsync(envelope);
        Assert.True(result.IsSuccess);

        var reloaded = await db.RentalOrders.Include(o => o.OperationReceipts).FirstAsync(o => o.Id == orderId);
        if (expectOnTime)
        {
            Assert.Equal(RentalOrderStatus.Paid, reloaded.Status);
            Assert.False(reloaded.LatePaymentRefundRequired);
            Assert.Equal(paidAt, reloaded.PaymentConfirmedAt);
            Assert.Null(reloaded.OperationReceipts.FirstOrDefault(r => r.Action == "RequestRefund"));
            Assert.Null(await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "LatePaymentRefundRequested"));
        }
        else
        {
            Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
            Assert.True(reloaded.LatePaymentRefundRequired);
            Assert.Equal(paidAt, reloaded.PaymentConfirmedAt);
            Assert.NotNull(reloaded.OperationReceipts.FirstOrDefault(r => r.Action == "RequestRefund"));
            Assert.NotNull(await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "LatePaymentRefundRequested"));
        }
    }

    [Fact]
    public async Task ProcessInvoicePaid_DefaultPaidAt_ReturnsValidationError()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateWorkflowService(db, new StubInventoryClient(), new StubPaymentClient());
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-default-paidat",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-INVALID", order.TotalPayableAmount, "VND", default));

        var result = await service.ProcessInvoicePaidAsync(envelope);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_PAID_AT", result.Error.Code);
    }

    [Fact]
    public async Task ProcessInvoicePaid_CancelledOrder_PaidBeforeDeadline_ProcessedBeforeDeadline_IsLatePayment_KeepsCancelledStatus_CreatesManualRefund()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        order.MarkCancelled(FixedNow.AddMinutes(5), "op-cancel", "User cancelled");
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        bool refundCalled = false;
        var paymentClient = new StubPaymentClient
        {
            RequestRefundHandler = _ =>
            {
                refundCalled = true;
                return Task.FromResult(Result.Success(new RequestRefundResponse("REF-003", "op", "Accepted", 100m, "VND", false)));
            }
        };

        var inventoryClient = new StubInventoryClient();
        var service = CreateWorkflowService(db, inventoryClient, paymentClient, FixedNow.AddMinutes(10));

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow.AddMinutes(10),
            "corr-3",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-CANCELLED", order.TotalPayableAmount, "VND", FixedNow.AddMinutes(6)));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);
        Assert.False(refundCalled);

        var reloaded = await db.RentalOrders.Include(o => o.OperationReceipts).FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Cancelled, reloaded.Status);
        Assert.True(reloaded.LatePaymentRefundRequired);

        var refundReceipt = reloaded.OperationReceipts.FirstOrDefault(r => r.Action == "RequestRefund");
        Assert.NotNull(refundReceipt);
        Assert.Equal(RentalOperationReceipt.StatusPending, refundReceipt.Status);
        Assert.False(refundReceipt.IsSuccess);
    }


    [Fact]
    public async Task ProcessInvoicePaid_ExtendHoldExpired_ExpiresItemAndSchedulesRefund()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateOrder(orderId, renterId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient
        {
            ExtendHoldHandler = (groupId, req) =>
                Task.FromResult(Result.Failure<ExtendHoldResponse>(Error.Conflict("HOLD_EXPIRED", "Hold on Inventory has expired.")))
        };

        var paymentClient = new StubPaymentClient
        {
            RequestRefundHandler = req => Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", req.OperationKey, "Accepted", req.TotalRefundAmount, "VND", false)))
        };

        var service = CreateWorkflowService(db, inventoryClient, paymentClient);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-001", "PAY-001", order.TotalPayableAmount, "VND", FixedNow));

        var result = await service.ProcessInvoicePaidAsync(envelope);

        Assert.True(result.IsSuccess);

        var replies = new InventoryReplySimulator(() => CreateDbContext(dbName), new TestTimeProvider(FixedNow), payment: paymentClient);
        var extendCmd = Assert.Single(await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ExtendHold));
        Assert.True((await replies.RejectAsync(extendCmd, "HOLD_EXPIRED", "Hold on Inventory has expired.")).IsSuccess);

        await using var verify = CreateDbContext(dbName);
        var reloaded = await verify.RentalOrders.Include(o => o.Items).Include(o => o.OperationReceipts).FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalItemStatus.Expired, reloaded.Items.First().Status);
        Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
        var refundReceipt = Assert.Single(reloaded.OperationReceipts, r => r.Action == "RequestRefund");
        Assert.Equal(RentalOperationReceipt.StatusPending, refundReceipt.Status);
        Assert.False(refundReceipt.IsSuccess);
    }
}
