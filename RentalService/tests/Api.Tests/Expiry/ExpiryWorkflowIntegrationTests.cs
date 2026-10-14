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
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Expiry;

public sealed class ExpiryWorkflowIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static RentalOrder CreatePendingPaymentOrder(Guid orderId, DateTimeOffset deadline)
    {
        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
            "Camera Sony",
            null,
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-EXP-001",
            Guid.NewGuid(),
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

        typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))?.SetValue(order, "INV-001");
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.PaymentDeadline))?.SetValue(order, deadline);

        return order;
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Func<CancelInvoiceRequest, Task<Result<CancelInvoiceResponse>>>? CancelHandler { get; set; }

        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) =>
            CancelHandler != null
                ? CancelHandler(request)
                : Task.FromResult(Result.Success(new CancelInvoiceResponse(request.InvoiceRef, true, FixedNow, false)));

        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }
        public Func<string, ReleaseAllocationRequest, Task<Result<ReleaseAllocationResponse>>>? ReleaseHandler { get; set; }

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            ReleaseHandler != null
                ? ReleaseHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null
                ? ConfirmHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingAvailabilityBatchResponse>> CheckListingAvailabilityBatchAsync(ListingAvailabilityBatchRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static ExpiryWorkflowService CreateExpiryService(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        DateTimeOffset? now = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(now ?? FixedNow);
        var config = new ConfigurationBuilder().Build();
        var executor = new RentalOperationExecutor(orderRepo, receiptRepo, outboxRepo, lockManager, inventoryClient, paymentClient, db, config, NullLogger<RentalOperationExecutor>.Instance, time);
        return new ExpiryWorkflowService(orderRepo, outboxRepo, lockManager, executor, db, config, NullLogger<ExpiryWorkflowService>.Instance, time);
    }

    private static RefundCompletedConsumer CreateRefundConsumer(RentalServiceDbContext db)
    {
        var orderRepo = new RentalOrderRepository(db);
        var inboxRepo = new InboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        return new RefundCompletedConsumer(orderRepo, inboxRepo, lockManager, db, NullLogger<RefundCompletedConsumer>.Instance, new TestTimeProvider(FixedNow));
    }

    [Fact]
    public async Task PaymentTimeout_CancelsInvoice_ExpiresOrderAndItems_ReleasesHolds()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        // Deadline in the past
        var order = CreatePendingPaymentOrder(orderId, FixedNow.AddMinutes(-10));
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        bool cancelCalled = false;
        var paymentClient = new StubPaymentClient
        {
            CancelHandler = req =>
            {
                cancelCalled = true;
                return Task.FromResult(Result.Success(new CancelInvoiceResponse(req.InvoiceRef, true, FixedNow, false)));
            }
        };

        bool releaseCalled = false;
        var inventoryClient = new StubInventoryClient
        {
            ReleaseHandler = (g, req) =>
            {
                releaseCalled = true;
                return Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", req.OperationKey, g, FixedNow, false)));
            }
        };

        var service = CreateExpiryService(db, inventoryClient, paymentClient);

        var result = await service.ExpireSingleOrderPaymentAsync(orderId);

        Assert.True(result.IsSuccess);
        Assert.True(cancelCalled);
        Assert.True(releaseCalled);
        var reloaded = await db.RentalOrders.Include(o => o.Items).FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Expired, reloaded.Status);
        Assert.Equal(RentalItemStatus.Expired, reloaded.Items.First().Status);

        var outbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "RentalOrderExpired");
        Assert.NotNull(outbox);
    }

    [Fact]
    public async Task PaymentTimeout_WhenInvoiceAlreadyPaid_HaltsExpiry_PreservesPendingPayment()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        var order = CreatePendingPaymentOrder(orderId, FixedNow.AddMinutes(-10));
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var paymentClient = new StubPaymentClient
        {
            CancelHandler = req =>
                Task.FromResult(Result.Failure<CancelInvoiceResponse>(Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Invoice was collected")))
        };

        var service = CreateExpiryService(db, new StubInventoryClient(), paymentClient);

        var result = await service.ExpireSingleOrderPaymentAsync(orderId);

        Assert.True(result.IsSuccess);

        // Invariant: INVOICE_ALREADY_PAID halts expiry! Order remains in PendingPayment awaiting validated event!
        var reloaded = await db.RentalOrders.FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.PendingPayment, reloaded.Status);
    }

    [Fact]
    public async Task SupplierTimeout_ExpiresItem_ReleasesHold_RequestsRefund()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        var order = CreatePendingPaymentOrder(orderId, FixedNow.AddMinutes(30));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        // Set deadline in the past
        typeof(RentalItem).GetProperty(nameof(RentalItem.SupplierConfirmationDeadline))?.SetValue(item, FixedNow.AddMinutes(-5));

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateExpiryService(db, new StubInventoryClient(), new StubPaymentClient());

        var result = await service.ExpireSingleItemSupplierTimeoutAsync(item.Id);

        Assert.True(result.IsSuccess);

        var reloaded = await db.RentalItems.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RentalItemStatus.Expired, reloaded.Status);

        var reloadedOrder = await db.RentalOrders.FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.Expired, reloadedOrder.Status);

        var outbox = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "RentalItemExpired");
        Assert.NotNull(outbox);
    }

    [Fact]
    public async Task SupplierTimeout_ReconcilesInFlightAcceptIntent_ClaimedBeforeDeadline()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        var order = CreatePendingPaymentOrder(orderId, FixedNow.AddMinutes(30));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        DateTimeOffset deadline = FixedNow.AddMinutes(-5);
        typeof(RentalItem).GetProperty(nameof(RentalItem.SupplierConfirmationDeadline))?.SetValue(item, deadline);

        // Add an in-flight accept intent claimed before deadline
        string inFlightKey = "op-in-flight-accept";
        var inFlightReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            orderId,
            inFlightKey,
            "SupplierAccept",
            "hash-accept",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: deadline.AddMinutes(-10), // Created before deadline!
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id);
        order.AddOperationReceipt(inFlightReceipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient
        {
            ConfirmHandler = (g, req) =>
            {
                Assert.Equal(inFlightKey, req.OperationKey);
                var units = item.UnitAssignments.Select(u => new ConfirmedAllocationUnitDto(u.AllocationId, u.AssetId, "Confirmed")).ToList();
                return Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-RECONCILED", g, FixedNow, false, units)));
            }
        };

        var service = CreateExpiryService(db, inventoryClient, new StubPaymentClient());

        var result = await service.ExpireSingleItemSupplierTimeoutAsync(item.Id);

        Assert.True(result.IsSuccess);

        // Reconciled as Accepted!
        var reloaded = await db.RentalItems.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RentalItemStatus.AwaitingHandover, reloaded.Status);
        Assert.Equal("REC-RECONCILED", reloaded.InventoryConfirmationRef);

        var reloadedOrder = await db.RentalOrders.FirstAsync(o => o.Id == orderId);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, reloadedOrder.Status);
    }

    [Fact]
    public async Task RefundCompleted_RecordsRefundRefAndAudit_PreservesRejectedStatus()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid orderId = Guid.NewGuid();
        var order = CreatePendingPaymentOrder(orderId, FixedNow.AddMinutes(30));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        item.ApplySupplierReject(item.SupplierId, FixedNow, "Busy", null, "op-rej-1");
        order.EvaluateOrderStatusRollup(FixedNow, "op-rej-1");

        var refundItems = new List<RefundItemRequest> { new(item.Id, 500_000m, 1_000_000m) };
        var refundReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            orderId,
            "refund:rejected:001",
            "RequestRefund",
            "hash-ref",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusPending,
            targetItemId: item.Id,
            payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(orderId, "INV-001", "PAY-001", refundItems, "Rejected", "refund:rejected:001")));
        order.AddOperationReceipt(refundReceipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var consumer = CreateRefundConsumer(db);

        var payload = new RefundCompletedPayload(
            orderId,
            "INV-001",
            "PAY-001",
            "REF-COMPLETED-001",
            "refund:rejected:001",
            1_500_000m,
            "VND",
            [new RefundCompletedItemPayload(item.Id, 500_000m, 1_000_000m)],
            FixedNow);

        var envelope = new KafkaMessageEnvelope<RefundCompletedPayload>(
            Guid.NewGuid(),
            "RefundCompleted",
            1,
            "payment-service",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            payload);

        var result = await consumer.ProcessRefundCompletedAsync(envelope);

        Assert.True(result.IsSuccess);

        var reloadedItem = await db.RentalItems.FirstAsync(i => i.Id == item.Id);
        Assert.Equal("REF-COMPLETED-001", reloadedItem.RefundRef);
        // Invariant: RefundCompleted does NOT change rejected/expired item to Completed!
        Assert.Equal(RentalItemStatus.Rejected, reloadedItem.Status);

        var reloadedOrder = await db.RentalOrders.Include(o => o.StateTransitions).FirstAsync(o => o.Id == orderId);
        Assert.Contains(reloadedOrder.StateTransitions, t => t.Action == "RefundCompleted");
    }
}
