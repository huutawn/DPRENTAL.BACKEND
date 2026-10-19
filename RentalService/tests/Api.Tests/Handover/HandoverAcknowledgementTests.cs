using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Handover;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Handover;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Handover;

/// <summary>
/// DPR-203: Người thuê xác nhận đã nhận thiết bị công trường ngoài công trường (Acknowledge Receipt).
/// </summary>
public sealed class HandoverAcknowledgementTests
{
    private readonly string _dbName = $"HandoverAck_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly AdjustableTimeProvider _clock = new(OrderTestData.Now.AddDays(1).AddHours(2));
    private RentalOrder _order = null!;
    private RentalItem _item = null!;

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private HandoverAcknowledgementService CreateService(RentalServiceDbContext context) => new(
        new RentalOrderRepository(context),
        new FakeOrderLockManager(),
        context,
        _clock,
        NullLogger<HandoverAcknowledgementService>.Instance);

    private async Task SeedAsync(bool withHandover = true, RentalItemStatus itemStatus = RentalItemStatus.InRental)
    {
        _order = OrderTestData.Paid(
            OrderTestData.Order(
                _renterId,
                "DH-2026-ACK-01",
                OrderTestData.Now,
                OrderTestData.Line(_supplierId, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m)),
            OrderTestData.Now.AddMinutes(5));

        _item = _order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(_item, itemStatus);

        if (withHandover)
        {
            OrderTestData.HandOver(_item, OrderTestData.Now.AddDays(1), "Đã giao máy tại công trường");
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(_item, itemStatus);
        }

        OrderTestData.LinkItemsToOrder(_order);

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(_order);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Renter_CanAcknowledgeReceipt_Successfully()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        AcknowledgeReceiptRequest request = new("Đã nhận máy đầm cóc tại công trường, máy chạy tốt");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(_renterId));

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(_item.Id, result.Value.ItemId);
        Assert.Equal(_clock.GetUtcNow(), result.Value.ReceivedAt);
        Assert.Equal(_renterId, result.Value.ReceivedBy);
        Assert.Equal("Đã nhận máy đầm cóc tại công trường, máy chạy tốt", result.Value.Note);

        await using RentalServiceDbContext verifyContext = NewContext();
        RentalItem itemInDb = await verifyContext.RentalItems
            .Include(i => i.HandoverRecord)
            .SingleAsync(i => i.Id == _item.Id);

        Assert.NotNull(itemInDb.HandoverRecord);
        Assert.Equal(_renterId, itemInDb.HandoverRecord.RenterAcknowledgedById);
        Assert.Equal(_clock.GetUtcNow(), itemInDb.HandoverRecord.RenterAcknowledgedAt);
        Assert.Equal("Đã nhận máy đầm cóc tại công trường, máy chạy tốt", itemInDb.HandoverRecord.RenterAcknowledgementNote);

        // Kiểm tra đúng 1 RentalStateTransition được ghi
        List<RentalStateTransition> transitions = await verifyContext.RentalStateTransitions
            .Where(t => t.RentalItemId == _item.Id && t.Action == "RenterAcknowledgeReceipt")
            .ToListAsync();
        Assert.Single(transitions);
        RentalStateTransition transition = transitions.Single();
        Assert.Equal("RenterAcknowledgeReceipt", transition.Action);
        Assert.Equal(ActorRole.Renter, transition.ActorRole);
        Assert.Equal(_renterId, transition.ActorId);
        Assert.Equal(RentalItemStatus.InRental.ToString(), transition.FromState);
        Assert.Equal(RentalItemStatus.InRental.ToString(), transition.ToState);

        // Trạng thái mục thuê không thay đổi (vẫn InRental)
        Assert.Equal(RentalItemStatus.InRental, itemInDb.Status);
    }

    [Fact]
    public async Task Replay_WithSameOrDifferentNote_IsIdempotentAndRetainsOriginalData()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context1 = NewContext();
        var service1 = CreateService(context1);

        AcknowledgeReceiptRequest firstRequest = new("Ghi chú lần đầu");
        Result<AcknowledgeReceiptResponse> firstResult = await service1.AcknowledgeAsync(
            _item.Id, firstRequest, OrderTestData.User(_renterId));
        Assert.True(firstResult.IsSuccess);

        // Tiến đồng hồ 1 giờ và gọi lại kèm ghi chú khác
        _clock.UtcNow = _clock.UtcNow.AddHours(1);

        await using RentalServiceDbContext context2 = NewContext();
        var service2 = CreateService(context2);

        AcknowledgeReceiptRequest secondRequest = new("Ghi chú lần hai muốn sửa");
        Result<AcknowledgeReceiptResponse> secondResult = await service2.AcknowledgeAsync(
            _item.Id, secondRequest, OrderTestData.User(_renterId));

        Assert.True(secondResult.IsSuccess);
        Assert.Equal(firstResult.Value.ReceivedAt, secondResult.Value.ReceivedAt);
        Assert.Equal(_renterId, secondResult.Value.ReceivedBy);
        Assert.Equal("Ghi chú lần đầu", secondResult.Value.Note); // Bằng chứng bất biến: giữ nguyên ghi chú đầu

        await using RentalServiceDbContext verifyContext = NewContext();
        // Không tạo thêm dòng lịch sử
        int transitionCount = await verifyContext.RentalStateTransitions
            .CountAsync(t => t.RentalItemId == _item.Id && t.Action == "RenterAcknowledgeReceipt");
        Assert.Equal(1, transitionCount);
    }

    [Fact]
    public async Task Stranger_CannotAcknowledgeReceipt_ReceivesNotFound()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        Guid strangerId = Guid.NewGuid();
        AcknowledgeReceiptRequest request = new("Người lạ nhận hộ");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(strangerId));

        Assert.Equal("Order.NotFound", result.Error.Code);

        await using RentalServiceDbContext verifyContext = NewContext();
        RentalItem itemInDb = await verifyContext.RentalItems
            .Include(i => i.HandoverRecord)
            .SingleAsync(i => i.Id == _item.Id);
        Assert.Null(itemInDb.HandoverRecord!.RenterAcknowledgedAt);
    }

    [Fact]
    public async Task SupplierOfItem_CannotAcknowledgeReceipt_ReceivesNotFound()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        AcknowledgeReceiptRequest request = new("Supplier tự xác nhận");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(_supplierId));

        // Supplier không phải là renter của đơn -> nhận NotFound để không phân biệt được với mã không tồn tại
        Assert.Equal("Order.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task ServiceToken_CannotAcknowledgeReceipt_ReceivesForbidden()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        AcknowledgeReceiptRequest request = new("Service token");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.Service());

        Assert.Equal(RentalAuthorizationPolicy.ErrorCodes.ServiceTokenNotAllowed, result.Error.Code);
    }

    [Fact]
    public async Task ItemWithoutHandoverRecord_ReceivesConflict()
    {
        // Chưa có biên bản bàn giao từ Supplier
        await SeedAsync(withHandover: false, itemStatus: RentalItemStatus.AwaitingHandover);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        AcknowledgeReceiptRequest request = new("Xác nhận trước");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(_renterId));

        Assert.Equal("HANDOVER_NOT_RECORDED", result.Error.Code);
    }

    [Fact]
    public async Task CompletedItem_CanStillBeAcknowledged_ReturnsOk()
    {
        // Dòng đã COMPLETED nhưng renter muốn xác nhận muộn để bổ sung bằng chứng
        await SeedAsync(withHandover: true, itemStatus: RentalItemStatus.Completed);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        AcknowledgeReceiptRequest request = new("Xác nhận bổ sung");
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(_renterId));

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(RentalItemStatus.Completed, _item.Status);
    }

    [Fact]
    public async Task NoteExceeding2000Characters_IsRejectedWithBadRequest()
    {
        await SeedAsync(withHandover: true);

        await using RentalServiceDbContext context = NewContext();
        var service = CreateService(context);

        string longNote = new('a', 2001);
        AcknowledgeReceiptRequest request = new(longNote);
        Result<AcknowledgeReceiptResponse> result = await service.AcknowledgeAsync(
            _item.Id, request, OrderTestData.User(_renterId));

        Assert.Equal("INVALID_NOTE", result.Error.Code);
    }

    [Fact]
    public async Task RenterOrderDetail_ReflectsAcknowledgementData()
    {
        await SeedAsync(withHandover: true);

        await using (RentalServiceDbContext context = NewContext())
        {
            var service = CreateService(context);
            Assert.True((await service.AcknowledgeAsync(
                _item.Id, new AcknowledgeReceiptRequest("Ghi chú kiểm tra mapper"), OrderTestData.User(_renterId))).IsSuccess);
        }

        await using (RentalServiceDbContext queryContext = NewContext())
        {
            RentalItem item = await queryContext.RentalItems
                .Include(i => i.HandoverRecord)
                .SingleAsync(i => i.Id == _item.Id);

            OrderHandoverResponse? handoverDto = OrderQueryMapper.ToHandover(item);
            Assert.NotNull(handoverDto);
            Assert.Equal(_clock.GetUtcNow(), handoverDto.RenterAcknowledgedAt);
            Assert.Equal(_renterId, handoverDto.RenterAcknowledgedById);
            Assert.Equal("Ghi chú kiểm tra mapper", handoverDto.RenterAcknowledgementNote);
        }
    }
}
