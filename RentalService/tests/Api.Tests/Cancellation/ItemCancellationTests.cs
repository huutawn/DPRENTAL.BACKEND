using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cancellation;

public sealed class ItemCancellationTests
{
    private readonly string _dbName = $"ItemCancel_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly AdjustableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(7)));
    private readonly Mock<IRentalOperationExecutor> _mockExecutor = new();
    private RentalOrder _order = null!;
    private RentalItem _item = null!;

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private ItemCancellationService CreateService(RentalServiceDbContext context) => new(
        new RentalOrderRepository(context),
        new FakeOrderLockManager(),
        _mockExecutor.Object,
        new OutboxRepository(context),
        context,
        _clock,
        NullLogger<ItemCancellationService>.Instance);

    private async Task SeedAsync(RentalItemStatus itemStatus = RentalItemStatus.AwaitingHandover)
    {
        _order = OrderTestData.Paid(
            OrderTestData.Order(
                _renterId,
                "DH-2026-CANCEL-01",
                OrderTestData.Now,
                OrderTestData.Line(_supplierId, "Máy đầm cóc Mikasa MT-77H", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m)),
            OrderTestData.Now.AddMinutes(5));

        _item = _order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(_item, itemStatus);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(_order, RentalOrderStatus.AwaitingHandover);

        OrderTestData.LinkItemsToOrder(_order);

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(_order);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CancelByRenter_BeforeOverdueMilestone_ReturnsCancelTooEarly409()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // Start is 2026-10-12. Overdue starts 2026-10-13 00:00 VN.
        // Set clock to within day 12 (before overdue)
        _clock.UtcNow = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.FromHours(7));

        await using var context = NewContext();
        var service = CreateService(context);

        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Chưa nhận được máy"),
            "idemp-key-01",
            OrderTestData.User(_renterId));

        Assert.True(result.IsFailure);
        Assert.Equal("CANCEL_TOO_EARLY", result.Error.Code);

        // Check DB was not modified
        var dbItem = await context.RentalItems.FindAsync(_item.Id);
        Assert.Equal(RentalItemStatus.AwaitingHandover, dbItem!.Status);
        Assert.Empty(context.RentalOperationReceipts.Where(r => r.Action == RentalItem.CancelNotDeliveredAction));
    }

    [Fact]
    public async Task CancelByRenter_AfterOverdueMilestone_SucceedsWith202AndSchedulesReleaseAndRefund()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // Clock is 2026-10-13 08:00 VN (past 00:00 milestone)
        await using var context = NewContext();
        var service = CreateService(context);

        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Không giao máy đầm"),
            "idemp-key-02",
            OrderTestData.User(_renterId));

        Assert.True(result.IsSuccess);
        Assert.Equal(_item.Id, result.Value.ItemId);
        Assert.Equal(RentalItemStatus.Cancelled.ToString(), result.Value.Status);
        decimal expectedRefund = _item.RentalAmount + _item.DepositAmount;
        Assert.Equal(expectedRefund, result.Value.RefundAmount);

        // Verify executor was called to schedule release and refund
        _mockExecutor.Verify(x => x.ScheduleReleaseAndRefundAsync(
            It.Is<RentalOrder>(o => o.Id == _order.Id),
            It.Is<RentalItem>(i => i.Id == _item.Id),
            _renterId,
            "Không giao máy đầm",
            _clock.GetUtcNow(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify DB changes
        var dbOrder = await context.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == _order.Id);
        Assert.Equal(RentalOrderStatus.Cancelled, dbOrder.Status);
        Assert.Equal(RentalItemStatus.Cancelled, dbOrder.Items.Single().Status);

        // Verify Receipt
        var receipt = await context.RentalOperationReceipts.SingleAsync(r => r.OperationId == "idemp-key-02");
        Assert.Equal(RentalItem.CancelNotDeliveredAction, receipt.Action);
        Assert.True(receipt.IsSuccess);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);

        // Verify Outbox message
        var outbox = await context.RentalOutboxMessages.SingleAsync(m => m.MessageType == "RentalItemCancelled");
        Assert.Equal(_order.Id.ToString("D"), outbox.PartitionKey);
    }

    [Fact]
    public async Task CancelByRenter_SameIdempotencyKeySamePayload_ReturnsCachedResponse()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var service = CreateService(context);

        var first = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Lý do cố định"),
            "idemp-key-repeat",
            OrderTestData.User(_renterId));
        Assert.True(first.IsSuccess);

        var second = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Lý do cố định"),
            "idemp-key-repeat",
            OrderTestData.User(_renterId));
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.CancelledAt, second.Value.CancelledAt);
        Assert.Equal(first.Value.RefundAmount, second.Value.RefundAmount);

        // Only 1 CancelNotDelivered receipt in DB
        Assert.Single(context.RentalOperationReceipts.Where(r => r.Action == RentalItem.CancelNotDeliveredAction));
    }

    [Fact]
    public async Task CancelByRenter_SameIdempotencyKeyDifferentPayload_ReturnsIdempotencyConflict409()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var service = CreateService(context);

        var first = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Lý do A"),
            "idemp-key-diff",
            OrderTestData.User(_renterId));
        Assert.True(first.IsSuccess);

        var second = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Lý do B khác hẳn"),
            "idemp-key-diff",
            OrderTestData.User(_renterId));

        Assert.True(second.IsFailure);
        Assert.Equal("IDEMPOTENCY_CONFLICT", second.Error.Code);
    }

    [Fact]
    public async Task CancelByRenter_DifferentKeyAfterCancelled_ReturnsCachedCancelledResponseIdempotently()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var service = CreateService(context);

        var first = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Hủy lần 1"),
            "idemp-key-first",
            OrderTestData.User(_renterId));
        Assert.True(first.IsSuccess);

        var second = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Gọi lại với key mới"),
            "idemp-key-second",
            OrderTestData.User(_renterId));

        Assert.True(second.IsSuccess);
        Assert.Equal(RentalItemStatus.Cancelled.ToString(), second.Value.Status);

        // Executor was NOT called a second time
        _mockExecutor.Verify(x => x.ScheduleReleaseAndRefundAsync(
            It.IsAny<RentalOrder>(),
            It.IsAny<RentalItem>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelByRenter_WhenItemIsInRental_ReturnsCancellationForbidden409()
    {
        await SeedAsync(RentalItemStatus.InRental);

        await using var context = NewContext();
        var service = CreateService(context);

        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Hủy"),
            "idemp-key-inrental",
            OrderTestData.User(_renterId));

        Assert.True(result.IsFailure);
        Assert.Equal("Order.CancellationForbidden", result.Error.Code);
    }

    [Fact]
    public async Task CancelByRenter_WhenHandoverInProgress_ReturnsHandoverInProgress409()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        // Add a pending MarkAssetsHandedOver receipt
        var pendingHandoverReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            _order.Id,
            "handover-op-key",
            "MarkAssetsHandedOver",
            "fp-handover",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: _clock.GetUtcNow(),
            status: RentalOperationReceipt.StatusPending,
            targetItemId: _item.Id);
        context.RentalOperationReceipts.Add(pendingHandoverReceipt);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Hủy khi đang giao"),
            "idemp-key-handover-pending",
            OrderTestData.User(_renterId));

        Assert.True(result.IsFailure);
        Assert.Equal("HANDOVER_IN_PROGRESS", result.Error.Code);
    }

    [Fact]
    public async Task CancelByRenter_StrangerCaller_ReturnsNotFound404()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var service = CreateService(context);

        Guid strangerId = Guid.NewGuid();
        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Hủy"),
            "idemp-key-stranger",
            OrderTestData.User(strangerId));

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.OrderNotFound.Code, result.Error.Code);
    }

    [Fact]
    public async Task CancelByRenter_ServiceTokenCaller_ReturnsForbidden403()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var service = CreateService(context);

        var result = await service.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Hủy"),
            "idemp-key-service",
            OrderTestData.Service());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }
}
