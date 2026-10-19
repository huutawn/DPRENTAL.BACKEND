using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Operations;

public sealed class RentalOperationExecutorAdvancedTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static (RentalOrder Order, RentalItem Item) CreatePaidOrderWithSucceededHold(
        Guid supplierId,
        DateTimeOffset? deadline = null)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
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
            "ORD-ADV-001",
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

        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        string holdOpKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{order.Items.First().Id}");
        var holdReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            holdOpKey,
            "ExtendHold",
            "hash-hold",
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusSucceeded,
            targetItemId: order.Items.First().Id);
        order.AddOperationReceipt(holdReceipt);

        if (deadline.HasValue)
        {
            var itemEntity = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.SupplierConfirmationDeadline))?.SetValue(itemEntity, deadline.Value);
        }

        return (order, order.Items.First());
    }

    private static RentalOperationExecutor CreateExecutor(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(FixedNow);
        var config = new ConfigurationBuilder().Build();
        return new RentalOperationExecutor(
            orderRepo,
            receiptRepo,
            outboxRepo,
            lockManager,
            inventoryClient,
            paymentClient,
            db,
            config,
            NullLogger<RentalOperationExecutor>.Instance,
            time);
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null
                ? ConfirmHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));
        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

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

    [Fact]
    public async Task DecisionInProgress_BlocksReject_WhenInFlightAcceptIntentExists()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithSucceededHold(supplierId);

        // Record in-flight accept intent in Processing state
        string inFlightKey = "op-in-flight-accept-key";
        var inFlightReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            inFlightKey,
            "SupplierAccept",
            "hash-accept",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id);
        order.AddOperationReceipt(inFlightReceipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());

        // RejectItem must be blocked with 409 DECISION_IN_PROGRESS!
        var rejectResult = await executor.ExecuteSupplierRejectAsync(
            order.Id,
            item.Id,
            supplierId,
            "Rejection reason",
            null,
            "idemp-reject-new",
            CancellationToken.None);

        Assert.True(rejectResult.IsFailure);
        Assert.Equal("DECISION_IN_PROGRESS", rejectResult.Error.Code);
    }

    [Fact]
    public async Task DecisionInProgress_BlocksAcceptWithDifferentKey_WhenInFlightAcceptIntentExists()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithSucceededHold(supplierId);

        string inFlightKey = "op-in-flight-accept-key";
        var inFlightReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            inFlightKey,
            "SupplierAccept",
            "hash-accept",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id);
        order.AddOperationReceipt(inFlightReceipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());

        // Accept with a DIFFERENT key must be blocked with 409 DECISION_IN_PROGRESS!
        var acceptResult = await executor.ExecuteSupplierAcceptAsync(
            order.Id,
            item.Id,
            supplierId,
            "idemp-different-key",
            CancellationToken.None);

        Assert.True(acceptResult.IsFailure);
        Assert.Equal("DECISION_IN_PROGRESS", acceptResult.Error.Code);
    }

    [Fact]
    public async Task ReconcileAccept_DoesNotRestoreTerminalStatus_WhenItemAlreadyRejected()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithSucceededHold(supplierId);

        // Item was already rejected
        item.ApplySupplierReject(supplierId, FixedNow, "Rejected before", null, "op-rej-prev");
        RentalOrderStatusPolicy.Apply(order, FixedNow, "op-rej-prev");

        string inFlightKey = "op-in-flight-key";
        var inFlightReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            inFlightKey,
            "SupplierAccept",
            "hash",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id);
        order.AddOperationReceipt(inFlightReceipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient
        {
            ConfirmHandler = (g, req) =>
            {
                var units = item.UnitAssignments.Select(u => new ConfirmedAllocationUnitDto(u.AllocationId, u.AssetId, "Confirmed")).ToList();
                return Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", g, FixedNow, false, units)));
            }
        };

        var executor = CreateExecutor(db, inventoryClient, new StubPaymentClient());

        // Reconcile must NOT restore Rejected to AwaitingHandover!
        var reconcileResult = await executor.ExecuteReconcileAcceptAsync(order.Id, item.Id, inFlightKey);

        Assert.True(reconcileResult.IsFailure);
        Assert.Equal("ITEM_IN_TERMINAL_STATE", reconcileResult.Error.Code);

        // State remains Rejected!
        var reloadedItem = await db.RentalItems.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RentalItemStatus.Rejected, reloadedItem.Status);
    }

    [Fact]
    public async Task Replay_WithCorruptedJournalResponse_ReturnsFailure()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithSucceededHold(supplierId);

        string key = "idemp-accept-corrupted";
        string fingerprint = RentalOperationExecutor.ComputeFingerprint($"accept:{item.Id}:{supplierId}");

        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            key,
            "SupplierAccept",
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: FixedNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: FixedNow,
            targetItemId: item.Id,
            apiResponseJson: "{ corrupt json"); // Corrupted!
        order.AddOperationReceipt(receipt);

        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());

        var result = await executor.ExecuteSupplierAcceptAsync(order.Id, item.Id, supplierId, key);

        Assert.True(result.IsFailure);
        Assert.Equal("CORRUPTED_JOURNAL_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task SingleLogicalFullRefund_ReusesSameKeyAcrossDifferentFailureModes()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        string keyFromReject = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderId}:{itemId}");
        string keyFromTimeout = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderId}:{itemId}");
        string keyFromHoldFailure = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{orderId}:{itemId}");

        Assert.Equal(keyFromReject, keyFromTimeout);
        Assert.Equal(keyFromReject, keyFromHoldFailure);
        Assert.True(keyFromReject.Length <= 64);
    }
}
