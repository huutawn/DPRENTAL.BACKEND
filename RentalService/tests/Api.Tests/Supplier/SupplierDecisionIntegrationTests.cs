using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
#pragma warning disable CA1859
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Supplier;

public sealed class SupplierDecisionIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private static (RentalOrder Order, RentalItem Item) CreatePaidOrderWithItem(
        Guid supplierId,
        DateTimeOffset? deadline = null,
        int quantity = 2)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var assignments = Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var item = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            quantity,
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
            "ORD-DEC-001",
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

    private static SupplierDecisionService CreateDecisionService(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient? paymentClient = null,
        DateTimeOffset? now = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(now ?? FixedNow);
        var config = new ConfigurationBuilder().Build();
        var executor = new RentalOperationExecutor(orderRepo, receiptRepo, outboxRepo, lockManager, inventoryClient, paymentClient ?? new StubPaymentClient(), db, config, NullLogger<RentalOperationExecutor>.Instance, time);
        var inventoryCommands = new InventoryCommandProducer(outboxRepo, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), time);
        return new SupplierDecisionService(orderRepo, executor, lockManager, inventoryCommands, db, time, NullLogger<SupplierDecisionService>.Instance);
    }

    private static IRentalCallerContext CreateUserCaller(Guid userId, string[]? roles = null)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, "true"),
            new(RentalAuthClaims.Roles, roles != null ? string.Join(",", roles) : "customer,owner")
        ];
        return RentalCallerContext.FromClaims(claims);
    }

    private static IRentalCallerContext CreateServiceCaller()
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
            new(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator),
            new(RentalAuthClaims.Roles, "service")
        ];
        return RentalCallerContext.FromClaims(claims);
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmHandler { get; set; }
        public Func<string, ReleaseAllocationRequest, Task<Result<ReleaseAllocationResponse>>>? ReleaseHandler { get; set; }

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmHandler != null
                ? ConfirmHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            ReleaseHandler != null
                ? ReleaseHandler(allocationGroupId, request)
                : Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(ListingRentalSnapshotsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
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

    private static InventoryReplySimulator Replies(string dbName) =>
        new(() => CreateDbContext(dbName), new TestTimeProvider(FixedNow));

    [Fact]
    public async Task AcceptSubOrder_ValidSupplier_SendsConfirm_ThenInventoryReplyMovesItemToAwaitingHandover()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid supplierId = Guid.NewGuid();
        Guid subOrderId;
        Guid orderId;

        await using (var seedDb = CreateDbContext(dbName))
        {
            var (order, item) = CreatePaidOrderWithItem(supplierId);
            orderId = order.Id;
            subOrderId = item.RentalSubOrderId;
            seedDb.RentalOrders.Add(order);
            await seedDb.SaveChangesAsync();
        }

        await using (var db = CreateDbContext(dbName))
        {
            var service = CreateDecisionService(db, new StubInventoryClient());
            var result = await service.AcceptSubOrderAsync(subOrderId, "idemp-accept-1", CreateUserCaller(supplierId));

            // Inventory xác nhận bất đồng bộ: lúc trả lời API, mục thuê vẫn chờ xác nhận và chưa phát sự kiện.
            Assert.True(result.IsSuccess, result.Error.Code);
            Assert.Equal("PendingSupplierConfirmation", result.Value.Status);
            Assert.Equal(subOrderId, result.Value.SubOrderId);
            Assert.False(await db.RentalOutboxMessages.AnyAsync(m => m.MessageType == "RentalItemConfirmed"));
        }

        InventoryReplySimulator replies = Replies(dbName);
        InventoryCommand confirm = Assert.Single(await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation));
        Assert.True((await replies.SucceedAsync(confirm)).IsSuccess);

        await using var verify = CreateDbContext(dbName);
        var reloadedOrder = await verify.RentalOrders.Include(o => o.Items).FirstAsync(o => o.Id == orderId);
        RentalItem accepted = reloadedOrder.Items.First();
        Assert.Equal(RentalItemStatus.AwaitingHandover, accepted.Status);
        Assert.Equal(supplierId, accepted.SupplierDecisionBy);

        var outbox = Assert.Single(await verify.RentalOutboxMessages.Where(m => m.MessageType == "RentalItemConfirmed").ToListAsync());
        Assert.Equal(orderId.ToString("D"), outbox.PartitionKey);
    }

    [Fact]
    public async Task AcceptSubOrder_WrongSupplier_ReturnsNotFound()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        Guid strangerSupplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateDecisionService(db, new StubInventoryClient());

        var strangerCaller = CreateUserCaller(strangerSupplierId);
        var result = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-1", strangerCaller);

        Assert.True(result.IsFailure);
        Assert.Equal("SUB_ORDER_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task AcceptSubOrder_ServiceCaller_ReturnsUnauthorized()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateDecisionService(db, new StubInventoryClient());

        var serviceCaller = CreateServiceCaller();
        var result = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-1", serviceCaller);

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_REQUIRED", result.Error.Code);
    }

    [Fact]
    public async Task AcceptSubOrder_AdminCallerNotOwner_ReturnsNotFound()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        Guid adminId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateDecisionService(db, new StubInventoryClient());

        // Admin caller who is NOT the supplier
        var adminCaller = CreateUserCaller(adminId, roles: ["admin", "adm"]);
        var result = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-1", adminCaller);

        Assert.True(result.IsFailure);
        Assert.Equal("SUB_ORDER_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task AcceptSubOrder_ReplaySameKey_Returns200OK()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var caller = CreateUserCaller(supplierId);
        var first = await CreateDecisionService(db, new StubInventoryClient())
            .AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-replay-1", caller);
        Assert.True(first.IsSuccess, first.Error.Code);

        // Gửi lại cùng khóa khi Inventory chưa trả lời: vẫn thành công, không ghi thêm lệnh xác nhận.
        var whilePending = await CreateDecisionService(db, new StubInventoryClient())
            .AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-replay-1", caller);
        Assert.True(whilePending.IsSuccess, whilePending.Error.Code);
        Assert.Equal("PendingSupplierConfirmation", whilePending.Value.Status);

        InventoryReplySimulator replies = Replies(dbName);
        InventoryCommand confirm = Assert.Single(await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation));
        Assert.True((await replies.SucceedAsync(confirm)).IsSuccess);

        // Gửi lại cùng khóa sau khi Inventory đã xác nhận: trả lại kết quả đã chốt.
        await using var afterReply = CreateDbContext(dbName);
        var second = await CreateDecisionService(afterReply, new StubInventoryClient())
            .AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-replay-1", caller);
        Assert.True(second.IsSuccess, second.Error.Code);
        Assert.Equal("AwaitingHandover", second.Value.Status);

        // Khóa khác sau khi đã quyết định -> 409 Conflict
        var differentKey = await CreateDecisionService(afterReply, new StubInventoryClient())
            .AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-diff-key", caller);
        Assert.True(differentKey.IsFailure);
        Assert.True(differentKey.Error.Code is "DECISION_IN_PROGRESS" or "ITEM_ALREADY_DECIDED");
    }

    [Fact]
    public async Task AcceptSubOrder_DeadlineExpired_ReturnsExpired()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        // Deadline set in the past
        var (order, item) = CreatePaidOrderWithItem(supplierId, deadline: FixedNow.AddMinutes(-5));
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateDecisionService(db, new StubInventoryClient());

        var caller = CreateUserCaller(supplierId);
        var result = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-1", caller);

        Assert.True(result.IsFailure);
        Assert.Equal("SUPPLIER_DEADLINE_EXPIRED", result.Error.Code);
    }

    [Fact]
    public async Task AcceptSubOrder_InventoryRejectsConfirm_KeepsItemPending_AndPublishesNoConfirmation()
    {
        string dbName = Guid.NewGuid().ToString("N");
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        await using (var db = CreateDbContext(dbName))
        {
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
            var result = await CreateDecisionService(db, new StubInventoryClient())
                .AcceptSubOrderAsync(item.RentalSubOrderId, "idemp-1", CreateUserCaller(supplierId));
            Assert.True(result.IsSuccess, result.Error.Code);
        }

        InventoryReplySimulator replies = Replies(dbName);
        InventoryCommand confirm = Assert.Single(await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation));
        Assert.True((await replies.RejectAsync(confirm, "INVALID_ALLOCATION", "Allocation không còn được giữ.")).IsSuccess);

        // Trạng thái phải giữ nguyên PendingSupplierConfirmation, phiếu nhận ghi lỗi của Inventory.
        await using var verify = CreateDbContext(dbName);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, (await verify.RentalItems.FirstAsync(i => i.Id == item.Id)).Status);
        RentalOperationReceipt receipt = await verify.RentalOperationReceipts.SingleAsync(r => r.OperationId == confirm.OperationKey || r.InventoryOperationKey == confirm.OperationKey);
        Assert.Equal(RentalOperationReceipt.StatusFailed, receipt.Status);
        Assert.Equal("INVALID_ALLOCATION", receipt.ErrorCode);
        Assert.False(await verify.RentalOutboxMessages.AnyAsync(m => m.MessageType == "RentalItemConfirmed"));
    }

    [Fact]
    public async Task RejectSubOrder_ValidReason_TransitionsToRejected_ReleasesUnits_SchedulesRefund()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var inventoryClient = new StubInventoryClient
        {
            ReleaseHandler = (groupId, req) =>
                Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", req.OperationKey, groupId, FixedNow, false)))
        };

        var service = CreateDecisionService(db, inventoryClient);

        var caller = CreateUserCaller(supplierId);
        var req = new SupplierRejectRequest("Equipment is undergoing maintenance", "Apologies for the inconvenience");
        var result = await service.RejectSubOrderAsync(item.RentalSubOrderId, req, "idemp-rej-1", caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("Rejected", result.Value.Status);
        Assert.Equal("Equipment is undergoing maintenance", Assert.Single(result.Value.Items).DecisionReason);

        var reloaded = await db.RentalItems.Include(i => i.UnitAssignments).FirstAsync(i => i.Id == item.Id);
        Assert.Equal(RentalItemStatus.Rejected, reloaded.Status);
        // Invariant: UnitAssignments remain Assigned until valid Inventory release receipt confirms release
        Assert.All(reloaded.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Assigned, u.Status));
        string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var releaseReceipt = await db.RentalOperationReceipts.FirstOrDefaultAsync(r => r.OperationId == releaseOpKey);
        Assert.NotNull(releaseReceipt);
        Assert.Equal("Pending", releaseReceipt.Status);

        // Execute release via Inventory ACK simulation to verify units become Released
        InventoryReplySimulator replies = Replies(dbName);
        InventoryCommand releaseCmd = Assert.Single(await replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ReleaseAllocation));
        Assert.True((await replies.SucceedAsync(releaseCmd)).IsSuccess);
        await using var verify = CreateDbContext(dbName);
        var reloadedAfterRelease = await verify.RentalItems.Include(i => i.UnitAssignments).FirstAsync(i => i.Id == item.Id);
        Assert.All(reloadedAfterRelease.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Released, u.Status));
        var outbox = await verify.RentalOutboxMessages.FirstOrDefaultAsync(m => m.MessageType == "RentalItemRejected");
    }

    [Fact]
    public async Task RejectSubOrder_InvalidReason_ReturnsValidationError()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);
        Guid supplierId = Guid.NewGuid();
        var (order, item) = CreatePaidOrderWithItem(supplierId);
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var service = CreateDecisionService(db, new StubInventoryClient());

        var caller = CreateUserCaller(supplierId);
        var req = new SupplierRejectRequest(""); // Empty reason
        var result = await service.RejectSubOrderAsync(item.RentalSubOrderId, req, "idemp-rej-empty", caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_REJECT_REASON", result.Error.Code);
    }
}
