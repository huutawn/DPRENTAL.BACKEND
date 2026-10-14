using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Supplier;

/// <summary>
/// Supplier nhận hoặc từ chối cả đơn con: đơn của An có máy xúc và máy đầm của Bình, máy trộn của Cường.
/// </summary>
public sealed class SubOrderDecisionTests
{
    private readonly string _dbName = $"SubOrderDecision_{Guid.NewGuid():N}";
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _cuong = Guid.NewGuid();
    private readonly ConfirmingInventoryClient _inventory = new();
    private readonly AdjustableTimeProvider _clock = new(OrderTestData.Now.AddMinutes(10));
    private RentalOrder _order = null!;

    [Fact]
    public async Task Accept_ConfirmsEveryLineOfTheSubOrder_AndLeavesTheOtherSupplierPending()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> result = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("AwaitingHandover", result.Value.Status);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(2, _inventory.ConfirmCalls.Count);

        List<RentalItem> items = await LoadItemsAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, items.Single(i => i.SupplierId == _cuong).Status);
    }

    [Fact]
    public async Task Accept_WhenOneLineIsNotReady_ConfirmsNothing()
    {
        await SeedAsync(extendHoldFor: item => item.ListingTitleSnapshot != "Máy đầm cóc Mikasa");

        Result<SubOrderDecisionResponse> result = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.Equal("HOLD_NOT_EXTENDED", result.Error.Code);
        Assert.Empty(_inventory.ConfirmCalls);
        Assert.All(await LoadItemsAsync(), i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));
    }

    [Fact]
    public async Task Accept_WhenInventoryFailsMidway_RetryWithTheSameKeyConfirmsOnlyTheRemainingLine()
    {
        await SeedAsync();
        _inventory.FailNextConfirms = 1;
        _inventory.FailAfterSuccesses = 1;

        Result<SubOrderDecisionResponse> first = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.Equal(ErrorType.ServiceUnavailable, first.Error.Type);
        List<RentalItem> afterFailure = await LoadItemsAsync();
        Assert.Equal(1, afterFailure.Count(i => i.SupplierId == _binh && i.Status == RentalItemStatus.AwaitingHandover));

        // Qua thời điểm thử lại rồi gửi lại cùng khóa.
        _clock.UtcNow += TimeSpan.FromMinutes(5);
        Result<SubOrderDecisionResponse> retry = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.True(retry.IsSuccess, retry.Error.Code);
        Assert.Equal("AwaitingHandover", retry.Value.Status);
        // Dòng đã xác nhận không gọi lại Inventory: 2 lần ở lượt đầu, 1 lần cho dòng còn thiếu.
        Assert.Equal(3, _inventory.ConfirmCalls.Count);
        Assert.All((await LoadItemsAsync()).Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
    }

    [Fact]
    public async Task Accept_OneSecondBeforeDeadline_ExpiryWorkerFinishesTheLineStillBeingConfirmed()
    {
        await SeedAsync();
        DateTimeOffset deadline = _order.Items.First().SupplierConfirmationDeadline!.Value;

        // Bình bấm nhận lúc 23:59:59: máy đầu chốt được, máy sau gặp Inventory chập chờn và đồng hồ đã qua hạn.
        _clock.UtcNow = deadline.AddSeconds(-1);
        _inventory.FailAfterSuccesses = 1;
        _inventory.FailNextConfirms = 1;
        _inventory.OnFailure = () => _clock.UtcNow = deadline.AddSeconds(1);

        Result<SubOrderDecisionResponse> accept = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));
        Assert.Equal(ErrorType.ServiceUnavailable, accept.Error.Type);

        // Phiếu "đang nhận" của cả hai máy đã được ghi trước hạn.
        await using (RentalServiceDbContext context = NewContext())
        {
            List<RentalOperationReceipt> intents = await context.RentalOperationReceipts
                .Where(r => r.Action == "SupplierAccept")
                .ToListAsync();
            Assert.Equal(2, intents.Count);
            Assert.All(intents, r => Assert.True(r.CreatedAt < deadline));
        }

        // Hai phút sau hạn, worker hết hạn chạy: máy của Bình được chốt nốt, máy của Cường (chưa ai nhận) hết hạn.
        _clock.UtcNow = deadline.AddMinutes(2);
        await ExpiryService().ProcessSupplierTimeoutsAsync();

        List<RentalItem> items = await LoadItemsAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
        Assert.Equal(RentalItemStatus.Expired, items.Single(i => i.SupplierId == _cuong).Status);
    }

    [Fact]
    public async Task Reject_RejectsEveryLine_SchedulesReleaseAndRefundForEach_AndReplaysWithTheSameKey()
    {
        await SeedAsync();
        SupplierRejectRequest request = new("Máy xúc đang bảo trì, không giao kịp");

        Result<SubOrderDecisionResponse> first = await Service().RejectSubOrderAsync(BinhsSubOrderId, request, "reject-1", OrderTestData.User(_binh));
        Result<SubOrderDecisionResponse> replay = await Service().RejectSubOrderAsync(BinhsSubOrderId, request, "reject-1", OrderTestData.User(_binh));
        Result<SubOrderDecisionResponse> otherReason = await Service().RejectSubOrderAsync(
            BinhsSubOrderId, new SupplierRejectRequest("Lý do khác"), "reject-1", OrderTestData.User(_binh));

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal("Rejected", first.Value.Status);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(first.Value.Items.Select(i => i.RentalItemId), replay.Value.Items.Select(i => i.RentalItemId));
        Assert.Equal("IDEMPOTENCY_CONFLICT", otherReason.Error.Code);

        await using RentalServiceDbContext context = NewContext();
        List<RentalItem> items = await context.RentalItems.ToListAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.Rejected, i.Status));
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, items.Single(i => i.SupplierId == _cuong).Status);

        List<RentalOperationReceipt> receipts = await context.RentalOperationReceipts.ToListAsync();
        Guid[] binhsItemIds = items.Where(i => i.SupplierId == _binh).Select(i => i.Id).Order().ToArray();
        Assert.Equal(binhsItemIds, receipts.Where(r => r.Action == "SupplierReject").Select(r => r.TargetItemId!.Value).Order());
        Assert.Equal(binhsItemIds, receipts.Where(r => r.Action == "ReleaseAllocation").Select(r => r.TargetItemId!.Value).Order());
        Assert.Equal(binhsItemIds, receipts.Where(r => r.Action == "RequestRefund").Select(r => r.TargetItemId!.Value).Order());
        Assert.Equal(2, await context.RentalOutboxMessages.CountAsync(m => m.MessageType == "RentalItemRejected"));
    }

    [Fact]
    public async Task Decisions_OnAnotherSuppliersSubOrder_AreNotFound()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> accept = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_cuong));
        Result<SubOrderDecisionResponse> reject = await Service().RejectSubOrderAsync(
            BinhsSubOrderId, new SupplierRejectRequest("Không có máy"), "reject-1", OrderTestData.User(_cuong));

        Assert.Equal("SUB_ORDER_NOT_FOUND", accept.Error.Code);
        Assert.Equal("SUB_ORDER_NOT_FOUND", reject.Error.Code);
        Assert.Empty(_inventory.ConfirmCalls);
        Assert.All(await LoadItemsAsync(), i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));
    }

    private Guid BinhsSubOrderId => _order.SubOrders.Single(s => s.SupplierId == _binh).Id;

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private SupplierDecisionService Service()
    {
        RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        RentalOperationExecutor executor = new(
            orders,
            new OperationReceiptRepository(context),
            new OutboxRepository(context),
            new FakeOrderLockManager(),
            _inventory,
            new FakePaymentRentalClient(),
            context,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            _clock);
        return new SupplierDecisionService(orders, executor, NullLogger<SupplierDecisionService>.Instance);
    }

    private ExpiryWorkflowService ExpiryService()
    {
        RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        OutboxRepository outbox = new(context);
        FakeOrderLockManager locks = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        RentalOperationExecutor executor = new(
            orders,
            new OperationReceiptRepository(context),
            outbox,
            locks,
            _inventory,
            new FakePaymentRentalClient(),
            context,
            configuration,
            NullLogger<RentalOperationExecutor>.Instance,
            _clock);
        return new ExpiryWorkflowService(
            orders, outbox, locks, executor, context, configuration, NullLogger<ExpiryWorkflowService>.Instance, _clock);
    }

    private async Task<List<RentalItem>> LoadItemsAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalItems.ToListAsync();
    }

    /// <param name="extendHoldFor">Dòng nào đã được gia hạn hold sau khi thanh toán; mặc định mọi dòng.</param>
    private async Task SeedAsync(Func<RentalItem, bool>? extendHoldFor = null)
    {
        _order = OrderTestData.Paid(
            OrderTestData.Order(
                Guid.NewGuid(),
                "DH-2026-000020",
                OrderTestData.Now,
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 1, 400_000m, 3_000_000m)),
            OrderTestData.Now.AddMinutes(5));

        foreach (RentalItem item in _order.Items.Where(extendHoldFor ?? (_ => true)))
        {
            _order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(),
                _order.Id,
                RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{_order.Id}:{item.Id}"),
                "ExtendHold",
                "hash-hold",
                isSuccess: true,
                errorCode: null,
                errorMessage: null,
                createdAt: OrderTestData.Now.AddMinutes(5),
                status: RentalOperationReceipt.StatusSucceeded,
                targetItemId: item.Id));
        }

        foreach (RentalItem item in _order.Items)
        {
            _inventory.UnitsByGroup[item.AllocationGroupId] = item.UnitAssignments
                .Select(u => new ConfirmedAllocationUnitDto(u.AllocationId, u.AssetId, "Confirmed"))
                .ToList();
        }

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(_order);
        await context.SaveChangesAsync();
    }

    /// <summary>Inventory giả: xác nhận đúng các unit đã giữ; có thể lỗi tạm thời sau một số lần thành công.</summary>
    private sealed class ConfirmingInventoryClient : IInventoryRentalClient
    {
        private int _successes;

        public Dictionary<string, List<ConfirmedAllocationUnitDto>> UnitsByGroup { get; } = [];

        public List<string> ConfirmCalls { get; } = [];

        public int FailAfterSuccesses { get; set; }

        public int FailNextConfirms { get; set; }

        /// <summary>Chạy ngay trước khi trả lỗi, ví dụ để cho đồng hồ trôi qua hạn xác nhận.</summary>
        public Action? OnFailure { get; set; }

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(
            string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default)
        {
            ConfirmCalls.Add(allocationGroupId);
            if (FailNextConfirms > 0 && _successes >= FailAfterSuccesses)
            {
                FailNextConfirms--;
                OnFailure?.Invoke();
                return Task.FromResult(Result.Failure<ConfirmAllocationResponse>(
                    Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Inventory tạm thời không phản hồi.")));
            }

            _successes++;
            return Task.FromResult(Result.Success(new ConfirmAllocationResponse(
                $"REC-{allocationGroupId}", allocationGroupId, OrderTestData.Now, false, UnitsByGroup[allocationGroupId])));
        }

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
