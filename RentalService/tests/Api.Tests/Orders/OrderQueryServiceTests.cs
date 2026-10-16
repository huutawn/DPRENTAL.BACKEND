using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// Người thuê xem đơn cha đầy đủ; nhà cung cấp chỉ xem đơn con của mình sau khi đơn đã thanh toán đúng hạn.
/// </summary>
public sealed class OrderQueryServiceTests
{
    // Giống API thật: tên ô camelCase; giữ nguyên chữ tiếng Việt để so sánh dễ đọc.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _dbName = $"OrderQueries_{Guid.NewGuid():N}";
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _otherRenter = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _cuong = Guid.NewGuid();

    // Đơn của An gồm máy xúc của Bình và 2 máy trộn của Cường, đã thanh toán.
    private RentalOrder _paidMixedOrder = null!;
    // Đơn mới hơn của An, chỉ có máy đầm của Bình, chưa thanh toán.
    private RentalOrder _unpaidOrder = null!;
    // Đơn của người thuê khác thuê máy xúc của Bình, đã thanh toán.
    private RentalOrder _otherRentersOrder = null!;
    // Đơn của An thanh toán trễ (sẽ được hoàn tiền), nhà cung cấp không được thấy.
    private RentalOrder _latePaidOrder = null!;

    [Fact]
    public async Task Renter_ListsOnlyOwnOrders_NewestFirst_WithTotalsAndCounts()
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> result =
            await RenterService().GetMyOrdersAsync(1, 20, OrderTestData.User(_an));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.TotalCount);
        Assert.Equal(
            [_latePaidOrder.OrderCode, _unpaidOrder.OrderCode, _paidMixedOrder.OrderCode],
            result.Value.Items.Select(o => o.OrderCode));

        RenterOrderSummaryResponse mixed = result.Value.Items.Single(o => o.OrderId == _paidMixedOrder.Id);
        // Máy xúc 3.000.000 x 3 ngày + 2 máy trộn x 400.000 x 3 ngày; cọc 20.000.000 + 2 x 3.000.000.
        Assert.Equal(11_400_000m, mixed.TotalRentalAmount);
        Assert.Equal(26_000_000m, mixed.TotalDepositAmount);
        Assert.Equal(37_400_000m, mixed.TotalAmount);
        Assert.Equal(2, mixed.ItemCount);
        Assert.Equal(2, mixed.SubOrderCount);
        Assert.Equal(RentalOrderStatus.Paid, mixed.Status);
    }

    [Fact]
    public async Task Renter_ListPaging_ReturnsRequestedPageAndClampsInvalidValues()
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> second =
            await RenterService().GetMyOrdersAsync(2, 2, OrderTestData.User(_an));
        Result<PagedResponse<RenterOrderSummaryResponse>> clamped =
            await RenterService().GetMyOrdersAsync(0, 500, OrderTestData.User(_an));

        Assert.Equal([_paidMixedOrder.OrderCode], second.Value.Items.Select(o => o.OrderCode));
        Assert.Equal(2, second.Value.TotalPages);
        Assert.Equal(1, clamped.Value.Page);
        Assert.Equal(100, clamped.Value.PageSize);
        Assert.Equal(3, clamped.Value.Items.Count);
    }

    [Fact]
    public async Task Renter_OrderDetail_ShowsEverySubOrderAndItem_WithInclusiveEndDate()
    {
        await SeedAsync();

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(_paidMixedOrder.Id, OrderTestData.User(_an));

        Assert.True(result.IsSuccess);
        RenterOrderDetailResponse order = result.Value;
        Assert.Equal(_paidMixedOrder.OrderCode, order.OrderCode);
        Assert.Equal(37_400_000m, order.TotalAmount);
        Assert.Equal($"INV-{_paidMixedOrder.OrderCode}", order.InvoiceRef);
        Assert.Equal("12 Võ Văn Ngân, Thủ Đức, TP.HCM", order.Delivery.Address);

        Assert.Equal(
            [$"{_paidMixedOrder.OrderCode}-01", $"{_paidMixedOrder.OrderCode}-02"],
            order.SubOrders.Select(s => s.SubOrderCode));

        RenterSubOrderResponse binh = order.SubOrders[0];
        Assert.Equal(_binh, binh.SupplierId);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, binh.Status);
        Assert.Equal(29_000_000m, binh.TotalAmount);
        // Mỗi dòng máy chỉ có tên, số lượng và ngày dùng đầu/cuối (có tính).
        OrderItemResponse excavator = Assert.Single(binh.Items);
        Assert.Equal(new OrderItemResponse("Máy xúc CAT 320", 1, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14)), excavator);
        Assert.Equal(
            """{"name":"Máy xúc CAT 320","quantity":1,"startDate":"2026-10-12","endDate":"2026-10-14"}""",
            JsonSerializer.Serialize(excavator, WebJson));

        RenterSubOrderResponse cuong = order.SubOrders[1];
        Assert.Equal(_cuong, cuong.SupplierId);
        Assert.Equal(2, Assert.Single(cuong.Items).Quantity);

        Assert.DoesNotContain("0123456789", JsonSerializer.Serialize(order), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renter_OrderOfAnotherRenter_IsNotFound()
    {
        await SeedAsync();

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(_otherRentersOrder.Id, OrderTestData.User(_an));

        Assert.True(result.IsFailure);
        Assert.Equal("Order.NotFound", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task Supplier_ListsOnlyOwnSubOrdersOfOrdersPaidOnTime_WithOwnTotals()
    {
        await SeedAsync();

        Result<PagedResponse<SupplierSubOrderSummaryResponse>> result =
            await SupplierService().GetMySubOrdersAsync(1, 20, OrderTestData.User(_binh));

        Assert.True(result.IsSuccess);
        // Không có đơn chưa thanh toán và đơn thanh toán trễ của An.
        Assert.Equal(
            [$"{_otherRentersOrder.OrderCode}-01", $"{_paidMixedOrder.OrderCode}-01"],
            result.Value.Items.Select(s => s.SubOrderCode));

        SupplierSubOrderSummaryResponse mixed = result.Value.Items.Single(s => s.OrderCode == _paidMixedOrder.OrderCode);
        Assert.Equal(9_000_000m, mixed.TotalRentalAmount);
        Assert.Equal(20_000_000m, mixed.TotalDepositAmount);
        Assert.Equal(1, mixed.ItemCount);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, mixed.Status);
        Assert.Equal(OrderTestData.Now.AddMinutes(5), mixed.PaidAt);
    }

    [Fact]
    public async Task Supplier_SubOrderDetail_ShowsOnlyOwnEquipmentAndDeliveryInfo()
    {
        await SeedAsync();
        Guid binhsSubOrderId = _paidMixedOrder.SubOrders.Single(s => s.SupplierId == _binh).Id;

        Result<SupplierSubOrderDetailResponse> result =
            await SupplierService().GetMySubOrderAsync(binhsSubOrderId, OrderTestData.User(_binh));

        Assert.True(result.IsSuccess);
        SupplierSubOrderDetailResponse subOrder = result.Value;
        Assert.Equal(_paidMixedOrder.OrderCode, subOrder.OrderCode);
        Assert.Equal("Máy xúc CAT 320", Assert.Single(subOrder.Items).Name);
        Assert.Equal(29_000_000m, subOrder.TotalAmount);
        Assert.Equal("Nguyễn Văn An", subOrder.Delivery.RecipientName);
        Assert.Equal("0901234567", subOrder.Delivery.RecipientPhone);

        string json = JsonSerializer.Serialize(subOrder);
        Assert.DoesNotContain("Máy trộn", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789", json, StringComparison.Ordinal);
        Assert.DoesNotContain("37400000", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supplier_SubOrderOfAnotherSupplierOrOfAnUnpaidOrder_IsNotFound()
    {
        await SeedAsync();
        Guid cuongsSubOrderId = _paidMixedOrder.SubOrders.Single(s => s.SupplierId == _cuong).Id;
        Guid unpaidSubOrderId = _unpaidOrder.SubOrders.Single().Id;

        Result<SupplierSubOrderDetailResponse> otherSupplier =
            await SupplierService().GetMySubOrderAsync(cuongsSubOrderId, OrderTestData.User(_binh));
        Result<SupplierSubOrderDetailResponse> unpaid =
            await SupplierService().GetMySubOrderAsync(unpaidSubOrderId, OrderTestData.User(_binh));

        Assert.Equal("SubOrder.NotFound", otherSupplier.Error.Code);
        Assert.Equal("SubOrder.NotFound", unpaid.Error.Code);
    }

    [Fact]
    public async Task Queries_RejectAnonymousAndServiceCallers()
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> anonymous =
            await RenterService().GetMyOrdersAsync(1, 20, OrderTestData.Anonymous());
        Result<PagedResponse<SupplierSubOrderSummaryResponse>> service =
            await SupplierService().GetMySubOrdersAsync(1, 20, OrderTestData.Service());

        Assert.Equal(ErrorType.Unauthorized, anonymous.Error.Type);
        Assert.Equal(ErrorType.Forbidden, service.Error.Type);
    }

    [Fact]
    public async Task Renter_SeesPickupLocation_OnlyAfterSupplierConfirmation()
    {
        await SeedAsync();

        // 1. Trước khi chủ kho xác nhận (Status = PendingSupplierConfirmation): renter không thấy địa chỉ
        Result<RenterOrderDetailResponse> beforeConfirm =
            await RenterService().GetMyOrderAsync(_paidMixedOrder.Id, OrderTestData.User(_an));
        Assert.True(beforeConfirm.IsSuccess);
        Assert.Null(beforeConfirm.Value.SubOrders[0].Items[0].PickupLocation);

        // 2. Chủ kho xác nhận: chuyển mục thuê sang AwaitingHandover
        await using (RentalServiceDbContext context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == _paidMixedOrder.Id);
            var item = order.Items.First(i => i.SupplierId == _binh);
            // Set status to AwaitingHandover
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.AwaitingHandover);
            await context.SaveChangesAsync();
        }

        // 3. Sau khi chủ xác nhận: renter nhìn thấy địa chỉ kho thật đã snapshot lúc checkout
        Result<RenterOrderDetailResponse> afterConfirm =
            await RenterService().GetMyOrderAsync(_paidMixedOrder.Id, OrderTestData.User(_an));
        Assert.True(afterConfirm.IsSuccess);
        Assert.Equal("Kho Thủ Đức, TP.HCM", afterConfirm.Value.SubOrders[0].Items[0].PickupLocation);
    }

    [Fact]
    public async Task Supplier_SeesPickupLocation_Directly()
    {
        await SeedAsync();

        var subOrder = _paidMixedOrder.SubOrders.First(s => s.SupplierId == _binh);

        Result<SupplierSubOrderDetailResponse> result =
            await SupplierService().GetMySubOrderAsync(subOrder.Id, OrderTestData.User(_binh));

        Assert.True(result.IsSuccess);
        Assert.Equal("Kho Thủ Đức, TP.HCM", result.Value.Items[0].PickupLocation);
    }

    private RenterOrderQueryService RenterService() => new(new RentalOrderRepository(NewContext()));

    private SupplierSubOrderQueryService SupplierService() => new(new RentalOrderRepository(NewContext()));

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task SeedAsync()
    {
        DateTimeOffset now = OrderTestData.Now;

        _paidMixedOrder = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000001",
                now,
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
            now.AddMinutes(5));

        _unpaidOrder = OrderTestData.Order(
            _an,
            "DH-2026-000002",
            now.AddHours(1),
            OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m));

        _otherRentersOrder = OrderTestData.Paid(
            OrderTestData.Order(
                _otherRenter,
                "DH-2026-000003",
                now.AddHours(2),
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m)),
            now.AddHours(2).AddMinutes(5));

        _latePaidOrder = OrderTestData.Order(
            _an,
            "DH-2026-000004",
            now.AddHours(3),
            OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m));
        _latePaidOrder.MarkAsLatePayment("INV-late", "PAY-late", now.AddHours(4), now.AddHours(4));

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.AddRange(_paidMixedOrder, _unpaidOrder, _otherRentersOrder, _latePaidOrder);
        await context.SaveChangesAsync();
    }
}
