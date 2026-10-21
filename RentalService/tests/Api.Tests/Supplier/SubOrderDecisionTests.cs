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
    }

    [Fact]
    public async Task Accept_SendsAConfirmForEveryLine_AndInventoryRepliesMoveOnlyThatSubOrderToAwaitingHandover()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> result = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        // Inventory xác nhận bất đồng bộ: lúc trả lời API, các dòng vẫn chờ xác nhận.
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("PendingSupplierConfirmation", result.Value.Status);
        Assert.Equal(2, result.Value.Items.Count);
        IReadOnlyList<InventoryCommand> confirms = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);
        Assert.Equal(BinhsItemIds, confirms.Select(c => c.RentalItemId).Order());

        await _replies.SucceedAllAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);

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
    public async Task Accept_ReplayWithTheSameKey_DoesNotResendConfirms_AndARejectedLineStaysPending()
    {
        await SeedAsync();

        Result<SubOrderDecisionResponse> first = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));
        Result<SubOrderDecisionResponse> replay = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        IReadOnlyList<InventoryCommand> confirms = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);
        Assert.Equal(2, confirms.Count);

        // Inventory chốt máy đầu, từ chối máy sau: chỉ máy đầu sang chờ bàn giao, phiếu của máy sau thất bại.
        Assert.True((await _replies.SucceedAsync(confirms[0])).IsSuccess);
        Assert.True((await _replies.RejectAsync(confirms[1], "ALLOCATION_NOT_HELD", "Hold đã bị nhả.")).IsSuccess);

        List<RentalItem> items = await LoadItemsAsync();
        Assert.Equal(RentalItemStatus.AwaitingHandover, items.Single(i => i.Id == confirms[0].RentalItemId).Status);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, items.Single(i => i.Id == confirms[1].RentalItemId).Status);
        await using RentalServiceDbContext context = NewContext();
        RentalOperationReceipt rejected = await context.RentalOperationReceipts.SingleAsync(r => r.OperationId == confirms[1].OperationKey || r.InventoryOperationKey == confirms[1].OperationKey);
        Assert.Equal(RentalOperationReceipt.StatusFailed, rejected.Status);
        Assert.Equal("ALLOCATION_NOT_HELD", rejected.ErrorCode);
    }

    [Fact]
    public async Task Accept_OneSecondBeforeDeadline_IsNotExpiredWhileInventoryIsStillConfirming()
    {
        await SeedAsync();
        DateTimeOffset deadline = _order.Items.First().SupplierConfirmationDeadline!.Value;

        // Bình bấm nhận lúc 23:59:59; Inventory chưa kịp trả lời trước hạn.
        _clock.UtcNow = deadline.AddSeconds(-1);
        Result<SubOrderDecisionResponse> accept = await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh));
        Assert.True(accept.IsSuccess, accept.Error.Code);

        // Phiếu "đang nhận" của cả hai máy đã được ghi trước hạn.
        await using (RentalServiceDbContext context = NewContext())
        {
            List<RentalOperationReceipt> intents = await context.RentalOperationReceipts
                .Where(r => r.Action == "SupplierAccept")
                .ToListAsync();
            Assert.Equal(2, intents.Count);
            Assert.All(intents, r => Assert.True(r.CreatedAt < deadline));
        }

        // Hai phút sau hạn, worker hết hạn chạy: máy của Bình đang được xác nhận nên không bị hủy,
        // máy của Cường (chưa ai nhận) hết hạn.
        _clock.UtcNow = deadline.AddMinutes(2);
        await ExpiryService().ProcessSupplierTimeoutsAsync();

        List<RentalItem> items = await LoadItemsAsync();
        Assert.All(items.Where(i => i.SupplierId == _binh), i => Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status));
        Assert.Equal(RentalItemStatus.Expired, items.Single(i => i.SupplierId == _cuong).Status);

        // Inventory trả lời muộn: các máy đã được nhận trước hạn được chốt.
        await _replies.SucceedAllAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);

        Assert.All((await LoadItemsAsync()).Where(i => i.SupplierId == _binh),
            i => Assert.Equal(RentalItemStatus.AwaitingHandover, i.Status));
    }

    [Fact]
    public async Task RedeliveredConfirmReply_AfterHandover_DoesNotMoveTheItemBack()
    {
        await SeedAsync();
        Assert.True((await Service().AcceptSubOrderAsync(BinhsSubOrderId, "accept-1", OrderTestData.User(_binh))).IsSuccess);
        IReadOnlyList<InventoryCommand> confirms = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);
        await _replies.SucceedAllAsync(KafkaContractSpec.CommandTypes.ConfirmAllocation);

        // Máy đã được bàn giao cho renter.
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalItem handedOver = await context.RentalItems.SingleAsync(i => i.Id == confirms[0].RentalItemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(handedOver, RentalItemStatus.InRental);
            await context.SaveChangesAsync();
        }

        // Inventory phát lại câu trả lời cũ với messageId mới.
        Result redelivered = await _replies.RedeliverSuccessAsync(confirms[0]);

        Assert.True(redelivered.IsSuccess, redelivered.Error.Code);
        Assert.Equal(RentalItemStatus.InRental, (await LoadItemsAsync()).Single(i => i.Id == confirms[0].RentalItemId).Status);
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
