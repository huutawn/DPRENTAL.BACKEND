using System.Text.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Payments;

public sealed class CheckoutRecoveryAndLatePaymentSagaTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreateMultiItemOrder(Guid orderId, Guid renterId, int itemCount = 2)
    {
        var items = new List<InitialRentalItemDto>();
        for (int i = 0; i < itemCount; i++)
        {
            var assignments = new List<InitialRentalUnitAssignmentDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            };
            items.Add(new InitialRentalItemDto(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 5),
                $"Device {i}",
                null,
                "Pickup",
                200_000m,
                500_000m,
                $"grp-item-{i}",
                assignments,
                TestListingSnapshots.Default));
        }

        var order = RentalOrder.Create(
            orderId,
            "ORD-SAGA-001",
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
            items).Value;

        typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))?.SetValue(order, "INV-SAGA-001");
        return order;
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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

    private sealed class TrackingPaymentClient : IPaymentRentalClient
    {
        public List<AcceptLatePaymentRequest> AcceptRequests { get; } = [];
        public bool ShouldRejectAccept { get; set; }

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<Result<AcceptLatePaymentResponse>> AcceptLatePaymentAsync(string invoiceRef, AcceptLatePaymentRequest request, CancellationToken ct = default)
        {
            AcceptRequests.Add(request);
            if (ShouldRejectAccept)
            {
                return Task.FromResult(Result.Failure<AcceptLatePaymentResponse>(
                    Error.Conflict("LATE_PAYMENT_WINDOW_EXPIRED", "Window expired.")));
            }
            return Task.FromResult(Result.Success(new AcceptLatePaymentResponse(invoiceRef, "PAID", false, DateTimeOffset.UtcNow)));
        }
    }

    [Fact]
    public async Task MultiItemOrder_LatePayment_WithinCutoff_RecoversAndCallsAcceptLatePayment()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateMultiItemOrder(orderId, renterId, itemCount: 2);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var paymentClient = new TrackingPaymentClient();
        DateTimeOffset lateTime = order.PaymentDeadline.AddMinutes(5);
        var workflowService = CreateWorkflowService(db, new StubInventoryClient(), paymentClient, lateTime);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            lateTime,
            "corr-late-1",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-SAGA-001", "PAY-LATE-001", order.TotalPayableAmount, "VND", lateTime,
                "LATE_REQUIRES_REFUND", lateTime));

        var applyResult = await workflowService.ProcessInvoicePaidAsync(envelope);
        Assert.True(applyResult.IsSuccess);

        // Verify that 2 RecoveryHold (CreateHold) commands were enqueued (one per item)
        var replies = new InventoryReplySimulator(() => CreateDbContext(dbName), new TestTimeProvider(lateTime), payment: paymentClient);
        var createCommands = await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.CreateHold);
        Assert.Equal(2, createCommands.Count);

        // Simulate Inventory succeeding both recovery holds
        foreach (var cmd in createCommands)
        {
            var mapping = new List<AllocationAssetMappingPayload> { new(Guid.NewGuid(), Guid.NewGuid(), "DEV-01") };
            Assert.True((await replies.SucceedAsync(cmd, allocations: mapping, allocationGroupId: $"rental-item:{cmd.RentalItemId}:rec-1")).IsSuccess);
        }
        Assert.Single(paymentClient.AcceptRequests);
        Assert.Equal(orderId, paymentClient.AcceptRequests[0].RentalOrderId);
        Assert.Equal("PAY-LATE-001", paymentClient.AcceptRequests[0].PaymentRef);

        // Verify that order is officially PAID and items are PendingSupplierConfirmation
        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Paid, reloaded.Status);
        Assert.All(reloaded.Items, item => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, item.Status));

        // Verify RentalOrderPaid and RentalSupplierConfirmationRequested outbox events were published
        var outboxes = await verifyDb.RentalOutboxMessages.ToListAsync();
        Assert.Contains(outboxes, m => m.MessageType == KafkaContractSpec.EventTypes.RentalOrderPaid);
        Assert.Equal(2, outboxes.Count(m => m.MessageType == KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested));
    }

    [Fact]
    public async Task MultiItemOrder_OneLineRecoveryFails_ReleasesAllHoldsAndStagesFullRefund()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateMultiItemOrder(orderId, renterId, itemCount: 2);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var paymentClient = new TrackingPaymentClient();
        DateTimeOffset lateTime = order.PaymentDeadline.AddMinutes(5);
        var workflowService = CreateWorkflowService(db, new StubInventoryClient(), paymentClient, lateTime);

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            lateTime,
            "corr-late-fail",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-SAGA-001", "PAY-LATE-FAIL", order.TotalPayableAmount, "VND", lateTime,
                "LATE_REQUIRES_REFUND", lateTime));

        var applyResult = await workflowService.ProcessInvoicePaidAsync(envelope);
        Assert.True(applyResult.IsSuccess);

        var replies = new InventoryReplySimulator(() => CreateDbContext(dbName), new TestTimeProvider(lateTime), payment: paymentClient);
        var createCommands = await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.CreateHold);
        Assert.Equal(2, createCommands.Count);
        // Line 1 succeeds recovery, Line 2 fails with INSUFFICIENT_AVAILABILITY
        var mapping1 = new List<AllocationAssetMappingPayload> { new(Guid.NewGuid(), Guid.NewGuid(), "DEV-01") };
        Assert.True((await replies.SucceedAsync(createCommands[0], allocations: mapping1, allocationGroupId: $"rental-item:{createCommands[0].RentalItemId}:rec-1")).IsSuccess);
        Assert.True((await replies.RejectAsync(createCommands[1], "INSUFFICIENT_AVAILABILITY", "Out of stock")).IsSuccess);
        Assert.Empty(paymentClient.AcceptRequests);

        // All-or-nothing: order is Expired, full refund is staged, releases for both lines are enqueued
        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalOrders.Include(o => o.Items).Include(o => o.OperationReceipts).SingleAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
        Assert.True(reloaded.LatePaymentRefundRequired);

        // Both items have RequestRefund staged
        var refundReceipts = reloaded.OperationReceipts.Where(r => r.Action == "RequestRefund").ToList();
        Assert.Equal(2, refundReceipts.Count);

        // Release allocation commands exist for line 1 (and initial group of line 2)
        var pendingReleases = await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ReleaseAllocation);
        Assert.NotEmpty(pendingReleases);
    }

    [Fact]
    public async Task CustomerCancelledOrder_LatePaymentArrives_AlwaysRefundsWithoutRecovery()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var order = CreateMultiItemOrder(orderId, renterId, itemCount: 1);
        order.MarkCancelled(FixedNow.AddMinutes(5), "op-user-cancel", "Customer decided to cancel");
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var paymentClient = new TrackingPaymentClient();
        var workflowService = CreateWorkflowService(db, new StubInventoryClient(), paymentClient, FixedNow.AddMinutes(10));

        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(),
            "InvoicePaid",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow.AddMinutes(10),
            "corr-cancelled",
            null,
            new InvoicePaidPayload(orderId, renterId, "INV-SAGA-001", "PAY-CANCEL-001", order.TotalPayableAmount, "VND", FixedNow.AddMinutes(8),
                "LATE_REQUIRES_REFUND", FixedNow.AddMinutes(10)));

        var applyResult = await workflowService.ProcessInvoicePaidAsync(envelope);
        Assert.True(applyResult.IsSuccess);

        // No recovery hold should be created!
        var replies = new InventoryReplySimulator(() => CreateDbContext(dbName), new TestTimeProvider(FixedNow.AddMinutes(10)), payment: paymentClient);
        var createCommands = await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.CreateHold);
        Assert.Empty(createCommands);

        // Order remains Cancelled and full refund is staged
        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Cancelled, reloaded.Status);
        Assert.True(reloaded.LatePaymentRefundRequired);

        var refundReceipt = Assert.Single(reloaded.OperationReceipts, r => r.Action == "RequestRefund");
        Assert.Equal(RentalOperationReceipt.StatusPending, refundReceipt.Status);
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
}
