using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Application.Kafka.Consumers;
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
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly AdjustableTimeProvider _clock = new(OrderTestData.Now.AddMinutes(10));
    private readonly InventoryReplySimulator _replies;
    private RentalOrder _order = null!;

    public SubOrderDecisionTests()
    {
        _replies = new InventoryReplySimulator(NewContext, _clock);
        _inventory.ConfirmAllocationHandler = (groupId, req) =>
        {
            var item = _order?.Items.FirstOrDefault(i => i.AllocationGroupId == groupId);
            var units = item?.UnitAssignments.Select(u => new ConfirmedAllocationUnitDto(u.AllocationId, u.AssetId, "Confirmed")).ToList()
                ?? new List<ConfirmedAllocationUnitDto>();
            return Task.FromResult(Result.Success(new ConfirmAllocationResponse(
                $"REC-{groupId}",
                groupId,
                DateTimeOffset.UtcNow,
                false,
                units)));
        };
    }

    [Fact]
    public async Task Accept_ConfirmsEveryLineSynchronously_AndMovesSubOrderToAwaitingHandover()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> result = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        // Inventory xác nhận đồng bộ: lúc trả lời API, các dòng của Bình đã sang AwaitingHandover.
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("AwaitingHandover", result.Value.Status);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.All(result.Value.Items, i => Assert.Equal("AwaitingHandover", i.Status));

        // Không còn phát lệnh ConfirmAllocation qua Kafka
        IReadOnlyList<InventoryCommand> confirms = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);
        Assert.Empty(confirms);

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
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation));
        Assert.All(await LoadItemsAsync(), i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));
    }

    [Fact]
    public async Task Accept_ReplayWithTheSameKey_ReturnsSameSuccessfulResult()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> first = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));
        Result<SubOrderDecisionResponse> replay = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal("AwaitingHandover", first.Value.Status);
        Assert.Equal("AwaitingHandover", replay.Value.Status);

        List<RentalItem> items = await LoadItemsAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
    }

    [Fact]
    public async Task Accept_OneSecondBeforeDeadline_SucceedsBeforeExpiryWorkerRuns()
    {
        await SeedAsync();
        DateTimeOffset deadline = _order.Items.First().SupplierConfirmationDeadline!.Value;

        // Bình bấm nhận lúc 23:59:59; xác nhận đồng bộ hoàn tất trước hạn.
        _clock.UtcNow = deadline.AddSeconds(-1);
        Result<SubOrderDecisionResponse> accept = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));
        Assert.True(accept.IsSuccess, accept.Error.Code);
        Assert.Equal("AwaitingHandover", accept.Value.Status);

        // Phiếu đã thành công trước hạn.
        await using (RentalServiceDbContext context = NewContext())
        {
            List<RentalOperationReceipt> intents = await context.RentalOperationReceipts
                .Where(r => r.Action == "SupplierAccept")
                .ToListAsync();
            Assert.Equal(2, intents.Count);
            Assert.All(intents, r => Assert.True(r.IsSuccess));
        }

        // Hai phút sau hạn, worker hết hạn chạy: máy của Bình đã AwaitingHandover nên không bị hủy,
        // máy của Cường (chưa ai nhận) hết hạn.
        _clock.UtcNow = deadline.AddMinutes(2);
        await ExpiryService().ProcessSupplierTimeoutsAsync();

        List<RentalItem> items = await LoadItemsAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
        Assert.Equal(RentalItemStatus.Expired, items.Single(i => i.SupplierId == _cuong).Status);
    }

    [Fact]
    public async Task RedeliveredConfirmReply_AfterHandover_DoesNotMoveTheItemBack()
    {
        await SeedAsync();
        Assert.True((await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh))).IsSuccess);

        List<RentalItem> binhItems = (await LoadItemsAsync()).Where(i => i.SupplierId == _binh).ToList();
        var itemToHandover = binhItems[0];

        // Máy đã được bàn giao cho renter.
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalItem handedOver = await context.RentalItems.SingleAsync(i => i.Id == itemToHandover.Id);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(handedOver, RentalItemStatus.InRental);
            await context.SaveChangesAsync();
        }

        // Event cũ từ Inventory gửi lại late qua handler
        await using (RentalServiceDbContext context = NewContext())
        {
            var handler = new InventoryAllocationResultHandler(
                new RentalOrderRepository(context),
                context,
                new FakeOrderLockManager(),
                _clock);

            var itemKey = RentalOperationExecutor.ComputeOperationKey("SubOrderAccept", $"{BinhsSubOrderId}:accept-1:{itemToHandover.Id}");
            var receipt = await context.RentalOperationReceipts.SingleAsync(r => r.OperationId == itemKey);
            var order = await context.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == _order.Id);

            var inbox = new RentalInboxMessage(
                Guid.NewGuid(), Guid.NewGuid(), KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
                KafkaContractSpec.Producers.InventoryService, _order.Id.ToString("D"), null, null,
                System.Text.Json.JsonSerializer.Serialize(new { payload = new { commandType = "ConfirmAllocation", operationKey = itemKey, rentalOrderId = _order.Id, rentalItemId = itemToHandover.Id } }),
                _clock.GetUtcNow());

            await handler.HandleAllocationEventAsync(
                _order.Id,
                inbox,
                order,
                CancellationToken.None);
        }
        Assert.Equal(RentalItemStatus.InRental, (await LoadItemsAsync()).Single(i => i.Id == itemToHandover.Id).Status);
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
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation));
        Assert.All(await LoadItemsAsync(), i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));
    }

    private Guid BinhsSubOrderId => _order.SubOrders.Single(s => s.SupplierId == _binh).Id;

    private Guid[] BinhsItemIds => _order.Items.Where(i => i.SupplierId == _binh).Select(i => i.Id).Order().ToArray();

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

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(_order);
        await context.SaveChangesAsync();
    }
}
