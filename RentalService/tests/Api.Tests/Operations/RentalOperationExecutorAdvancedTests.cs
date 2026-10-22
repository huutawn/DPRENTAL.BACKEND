using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Domain.IRepositories;
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
using Moq;
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
    private static RentalOrder CreatePendingOrder(
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

        return RentalOrder.Create(
            orderId,
            "ORD-ADV-001",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "ORIGINAL_PAYMENT_SOURCE",
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [item],
            paymentDeadline: deadline ?? FixedNow.AddMinutes(30)).Value;
    }


    private static RentalOperationExecutor CreateExecutor(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient paymentClient,
        ICheckoutInvoiceIssuer? invoiceIssuer = null,
        IRentalCheckoutOperationRepository? checkoutOpRepo = null,
        TimeProvider? timeProvider = null,
        IInventoryCommandProducer? inventoryCommands = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = timeProvider ?? new TestTimeProvider(FixedNow);
        var config = new ConfigurationBuilder().Build();
        return new RentalOperationExecutor(
            orderRepo,
            receiptRepo,
            outboxRepo,
            lockManager,
            inventoryClient,
            paymentClient,
            inventoryCommands: inventoryCommands!,
            unitOfWork: db,
            configuration: config,
            logger: NullLogger<RentalOperationExecutor>.Instance,
            invoiceIssuer: invoiceIssuer,
            checkoutOperationRepo: checkoutOpRepo,
            timeProvider: time);
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null
                ? ConfirmHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public static Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public static Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));
        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Func<CancelInvoiceRequest, Task<Result<CancelInvoiceResponse>>>? CancelInvoiceHandler { get; set; }

        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) =>
            CancelInvoiceHandler?.Invoke(request) ?? throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    [Fact]
    public async Task RenterCancelOrder_ForeignRenterGetsNotFoundWithoutChangingOrder()
    {
        await using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var order = CreatePendingOrder(Guid.NewGuid());
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());
        var result = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, Guid.NewGuid(), "Changed plans", "cancel-foreign-1");

        Assert.True(result.IsFailure);
        Assert.Equal("Order.NotFound", result.Error.Code);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public async Task RenterCancelOrder_PaidOrderIsNotCancelled()
    {
        await using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var order = CreatePendingOrder(Guid.NewGuid());
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());
        var result = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, order.RenterId, "Changed plans", "cancel-paid-1");

        Assert.True(result.IsFailure);
        Assert.Equal("Order.NotPendingPayment", result.Error.Code);
        Assert.Equal(RentalOrderStatus.Paid, order.Status);
    }

    [Fact]
    public async Task RenterCancelOrder_QueuesReleaseAndReplaysSameKeyWithoutDuplicateEvent()
    {
        await using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var order = CreatePendingOrder(Guid.NewGuid());
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id,
            RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString()),
            "CreateInvoice", "create-invoice-fingerprint", false, null, null,
            FixedNow, RentalOperationReceipt.StatusPending));
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var producer = new Mock<IInventoryCommandProducer>();
        producer.Setup(p => p.EnqueueReleaseAllocationAsync(
                It.IsAny<ReleaseAllocationCommandPayload>(), It.IsAny<string?>(),
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ReleaseAllocation", "inventory.commands", "order")));

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient(),
            inventoryCommands: producer.Object);
        var first = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, order.RenterId, "Changed plans", "cancel-request-1");
        var replay = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, order.RenterId, "Changed plans", "cancel-request-1");
        var conflict = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, order.RenterId, "Different reason", "cancel-request-1");

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
        Assert.All(order.Items.Single().UnitAssignments,
            unit => Assert.Equal(RentalUnitAssignmentStatus.Assigned, unit.Status));
        Assert.True(conflict.IsFailure);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);
        Assert.Single(db.RentalOutboxMessages);
        producer.Verify(p => p.EnqueueReleaseAllocationAsync(
            It.IsAny<ReleaseAllocationCommandPayload>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenterCancelOrder_InvoiceAlreadyPaidStopsBeforeRelease()
    {
        await using var db = CreateDbContext(Guid.NewGuid().ToString("N"));
        var order = CreatePendingOrder(Guid.NewGuid());
        Assert.True(order.AttachInvoice("INV-PAID", FixedNow).IsSuccess);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var payment = new StubPaymentClient
        {
            CancelInvoiceHandler = _ => Task.FromResult<Result<CancelInvoiceResponse>>(
                Error.Conflict("INVOICE_ALREADY_PAID", "Invoice has been paid"))
        };
        var producer = new Mock<IInventoryCommandProducer>();
        var executor = CreateExecutor(db, new StubInventoryClient(), payment,
            inventoryCommands: producer.Object);

        var result = await executor.ExecuteRenterCancelOrderAsync(
            order.Id, order.RenterId, "Changed plans", "cancel-paid-invoice-1");

        Assert.True(result.IsFailure);
        Assert.Equal("INVOICE_ALREADY_PAID", result.Error.Code);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.All(order.Items.Single().UnitAssignments,
            unit => Assert.Equal(RentalUnitAssignmentStatus.Assigned, unit.Status));
        producer.VerifyNoOtherCalls();
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
    [Fact]
    public async Task CreateInvoice_Exceeding10Attempts_StillPickedUpByQueryAndContinuesRetry()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var order = CreatePendingOrder(Guid.NewGuid(), FixedNow.AddMinutes(20));

        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "CreateInvoice", "fp-1",
            isSuccess: false, errorCode: "Payment.Http503", errorMessage: "Unavailable",
            createdAt: FixedNow.AddMinutes(-5),
            status: RentalOperationReceipt.StatusPending,
            retryCount: 10,
            lastAttemptedAt: FixedNow.AddMinutes(-1),
            nextRetryAt: FixedNow.AddSeconds(-5),
            isTransient: true);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var receiptRepo = new OperationReceiptRepository(db);
        var pending = await receiptRepo.GetPendingOperationDescriptorsAsync(
            FixedNow, 50, unlimitedAttemptActions: ["CreateInvoice"]);
        var desc = Assert.Single(pending, p => p.Action == "CreateInvoice");
        Assert.Equal(10, desc.RetryCount);

        bool paymentCalled = false;
        var fakePayment = new StubPaymentClient();
        var fakeIssuer = new FakeInvoiceIssuer
        {
            Handler = o =>
            {
                paymentCalled = true;
                o.AttachInvoice("INV-RETRY-10", FixedNow);
                o.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey)?.MarkSucceeded(FixedNow);
                return Task.FromResult(Result.Success());
            }
        };
        var executor = CreateExecutor(db, new StubInventoryClient(), fakePayment, invoiceIssuer: fakeIssuer);
        var res = await executor.ExecuteScheduledOperationAsync(desc);

        Assert.True(res.IsSuccess, res.IsFailure ? res.Error.ToString() : null);
        Assert.True(paymentCalled);
        var reloaded = await db.RentalOperationReceipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(11, reloaded.RetryCount);
        Assert.True(reloaded.IsSuccess);
    }

    [Fact]
    public async Task OtherActions_StoppingAt10Attempts_NotPickedUpByQueryAndNotExecuted()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var (order, item) = CreatePaidOrderWithSucceededHold(Guid.NewGuid());

        string opKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "ReleaseAllocation", "fp-rel",
            isSuccess: false, errorCode: "Inventory.Unavailable", errorMessage: "Unavailable",
            createdAt: FixedNow.AddMinutes(-5),
            status: RentalOperationReceipt.StatusPending,
            retryCount: 10,
            nextRetryAt: FixedNow.AddSeconds(-5),
            targetItemId: item.Id,
            isTransient: true);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var receiptRepo = new OperationReceiptRepository(db);
        var pending = await receiptRepo.GetPendingOperationDescriptorsAsync(
            FixedNow, 50, unlimitedAttemptActions: ["CreateInvoice"]);
        Assert.DoesNotContain(pending, p => p.Action == "ReleaseAllocation");

        var desc = new OperationReceiptDescriptor(
            receipt.Id, order.Id, opKey, "ReleaseAllocation", item.Id,
            RentalOperationReceipt.StatusPending, 10, receipt.NextRetryAt, receipt.CreatedAt, null, false);

        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient());
        var res = await executor.ExecuteScheduledOperationAsync(desc);
        Assert.True(res.IsSuccess);
        Assert.Equal(10, receipt.RetryCount); // Not incremented!
    }

    [Fact]
    public async Task Recovery_OldExhaustedReceipt_RecoveredIfPendingPaymentAndBeforeDeadline()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var order = CreatePendingOrder(Guid.NewGuid(), FixedNow.AddMinutes(20));

        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "CreateInvoice", "fp-exh",
            isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
            createdAt: FixedNow.AddMinutes(-10),
            status: RentalOperationReceipt.StatusFailed,
            retryCount: 10,
            nextRetryAt: null,
            errorClassification: RentalOperationReceipt.ClassificationTransientExhausted,
            isTransient: false);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var receiptRepo = new OperationReceiptRepository(db);
        var pending = await receiptRepo.GetPendingOperationDescriptorsAsync(
            FixedNow, 50, unlimitedAttemptActions: ["CreateInvoice"]);
        var desc = Assert.Single(pending, p => p.OperationId == opKey);
        Assert.Equal(RentalOperationReceipt.StatusFailed, desc.Status);

        bool called = false;
        var fakeIssuer = new FakeInvoiceIssuer
        {
            Handler = o =>
            {
                called = true;
                o.AttachInvoice("INV-RECOVERED", FixedNow);
                o.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey)?.MarkSucceeded(FixedNow);
                return Task.FromResult(Result.Success());
            }
        };
        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient(), invoiceIssuer: fakeIssuer);
        var res = await executor.ExecuteScheduledOperationAsync(desc);

        Assert.True(res.IsSuccess, res.IsFailure ? res.Error.ToString() : null);
        Assert.True(called);
        var reloaded = await db.RentalOperationReceipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(11, reloaded.RetryCount);
        Assert.True(reloaded.IsSuccess);
    }

    [Fact]
    public async Task Recovery_OldExhaustedReceipt_ClosedIfOrderExpired()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var order = CreatePendingOrder(Guid.NewGuid(), FixedNow.AddMinutes(20));
        order.MarkPaymentExpired(FixedNow.AddMinutes(-5), "op-expire");

        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "CreateInvoice", "fp-exh",
            isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
            createdAt: FixedNow.AddMinutes(-10),
            status: RentalOperationReceipt.StatusFailed,
            retryCount: 10,
            errorClassification: RentalOperationReceipt.ClassificationTransientExhausted);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var desc = new OperationReceiptDescriptor(
            receipt.Id, order.Id, opKey, "CreateInvoice", null,
            RentalOperationReceipt.StatusFailed, 10, null, receipt.CreatedAt, null, false);

        bool called = false;
        var fakeIssuer = new FakeInvoiceIssuer { Handler = _ => { called = true; return Task.FromResult(Result.Success()); } };
        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient(), invoiceIssuer: fakeIssuer);
        var res = await executor.ExecuteScheduledOperationAsync(desc);

        Assert.True(res.IsSuccess);
        Assert.False(called);
        Assert.Equal(10, receipt.RetryCount);
        Assert.Equal(RentalOperationReceipt.StatusFailed, receipt.Status);
    }

    [Fact]
    public async Task Recovery_OldExhaustedReceipt_CompletedLocallyIfOrderHasInvoice()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var order = CreatePendingOrder(Guid.NewGuid(), FixedNow.AddMinutes(20));
        order.AttachInvoice("INV-EXISTING", FixedNow);
        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "CreateInvoice", "fp-exh",
            isSuccess: false, errorCode: "Payment.Timeout", errorMessage: "Timeout",
            createdAt: FixedNow.AddMinutes(-10),
            status: RentalOperationReceipt.StatusFailed,
            retryCount: 10,
            errorClassification: RentalOperationReceipt.ClassificationTransientExhausted);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var desc = new OperationReceiptDescriptor(
            receipt.Id, order.Id, opKey, "CreateInvoice", null,
            RentalOperationReceipt.StatusFailed, 10, null, receipt.CreatedAt, null, false);

        bool issuerCalled = false;
        var fakePayment = new StubPaymentClient();
        var fakeIssuer = new FakeInvoiceIssuer
        {
            Handler = o =>
            {
                issuerCalled = true;
                o.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey)?.MarkSucceeded(FixedNow);
                return Task.FromResult(Result.Success());
            }
        };
        var executor = CreateExecutor(db, new StubInventoryClient(), fakePayment, invoiceIssuer: fakeIssuer);
        var res = await executor.ExecuteScheduledOperationAsync(desc);

        Assert.True(res.IsSuccess);
        Assert.True(issuerCalled);
        Assert.Equal(10, receipt.RetryCount);
        var reloaded = await db.RentalOperationReceipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.True(reloaded.IsSuccess);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, reloaded.Status);
    }

    [Fact]
    public async Task Recovery_DeterministicBusinessFailedReceipt_NotRecovered()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        var order = CreatePendingOrder(Guid.NewGuid(), FixedNow.AddMinutes(20));

        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        var receipt = new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, opKey, "CreateInvoice", "fp-bus",
            isSuccess: false, errorCode: "Checkout.InvoiceRejected", errorMessage: "Rejected",
            createdAt: FixedNow.AddMinutes(-10),
            status: RentalOperationReceipt.StatusFailed,
            retryCount: 1,
            errorClassification: RentalOperationReceipt.ClassificationBusiness);
        order.AddOperationReceipt(receipt);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var receiptRepo = new OperationReceiptRepository(db);
        var pending = await receiptRepo.GetPendingOperationDescriptorsAsync(
            FixedNow, 50, unlimitedAttemptActions: ["CreateInvoice"]);
        Assert.DoesNotContain(pending, p => p.OperationId == opKey);

        var desc = new OperationReceiptDescriptor(
            receipt.Id, order.Id, opKey, "CreateInvoice", null,
            RentalOperationReceipt.StatusFailed, 1, null, receipt.CreatedAt, null, false);

        bool called = false;
        var fakeIssuer = new FakeInvoiceIssuer { Handler = _ => { called = true; return Task.FromResult(Result.Success()); } };
        var executor = CreateExecutor(db, new StubInventoryClient(), new StubPaymentClient(), invoiceIssuer: fakeIssuer);
        var res = await executor.ExecuteScheduledOperationAsync(desc);

        Assert.True(res.IsSuccess);
        Assert.False(called);
        Assert.Equal(RentalOperationReceipt.StatusFailed, receipt.Status);
    }

    private sealed class FakeInvoiceIssuer : ICheckoutInvoiceIssuer
    {
        public Func<RentalOrder, Task<Result>>? Handler { get; set; }
        public async Task<Result<CreateInvoiceResponse>> EnsureInvoiceAsync(RentalOrder order, CancellationToken cancellationToken = default)
        {
            if (Handler != null)
            {
                var res = await Handler(order);
                if (res.IsFailure) return res.Error;
            }
            return new CreateInvoiceResponse("INV-TEST", order.TotalPayableAmount, order.Currency, order.PaymentDeadline, "Pending", "https://qr", "Bank", "Account", "ORD", false);
        }
    }
}
