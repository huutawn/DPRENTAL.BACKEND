using System.Globalization;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// Admin tìm mọi đơn theo mã, dòng, thiết bị, máy, nhà cung cấp, người thuê, trạng thái và ngày đặt;
/// xem chi tiết kèm mã nội bộ và lịch sử trạng thái.
/// </summary>
public sealed class AdminOrderQueryServiceTests
{
    private readonly string _dbName = $"AdminOrders_{Guid.NewGuid():N}";
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _dung = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _cuong = Guid.NewGuid();

    // Đơn của An: Bình từ chối máy xúc và máy đầm (máy đầm đã hoàn tiền); Cường giao 2 máy trộn, một máy vỡ thùng.
    private RentalOrder _lifecycleOrder = null!;
    // Đơn của An đặt sau 1 ngày: máy xúc của Bình và máy trộn của Cường, đã thanh toán, chờ nhà cung cấp xác nhận.
    private RentalOrder _waitingOrder = null!;
    // Đơn của Dũng đặt sau 2 ngày: máy đầm của Bình, chưa thanh toán.
    private RentalOrder _unpaidOrder = null!;

    [Fact]
    public async Task Search_WithoutFilters_ReturnsEveryOrderNewestFirst_WithRenterSuppliersAndInvoice()
    {
        await SeedAsync();

        Result<PagedResponse<AdminOrderSummaryResponse>> result = await Search(new AdminOrderSearchRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [_unpaidOrder.OrderCode, _waitingOrder.OrderCode, _lifecycleOrder.OrderCode],
            result.Value.Items.Select(o => o.OrderCode));

        AdminOrderSummaryResponse waiting = result.Value.Items[1];
        Assert.Equal(_an, waiting.RenterId);
        Assert.Equal([_binh, _cuong], waiting.SupplierIds);
        Assert.Equal($"INV-{_waitingOrder.OrderCode}", waiting.InvoiceRef);
        Assert.Equal(2, waiting.ItemCount);
        Assert.Equal(37_400_000m, waiting.TotalAmount);
        Assert.Null(result.Value.Items[0].InvoiceRef);
    }

    [Fact]
    public async Task Search_FindsOrdersByEachIdentifier()
    {
        await SeedAsync();
        RentalItem excavator = _waitingOrder.Items.Single(i => i.SupplierId == _binh);
        RentalItem mixers = _lifecycleOrder.Items.Single(i => i.SupplierId == _cuong);

        Assert.Equal([_waitingOrder.OrderCode], await Codes(new() { OrderCode = $" {_waitingOrder.OrderCode.ToLowerInvariant()} " }));
        Assert.Equal([_lifecycleOrder.OrderCode], await Codes(new() { SubOrderCode = $"{_lifecycleOrder.OrderCode}-02" }));
        Assert.Equal([_waitingOrder.OrderCode], await Codes(new() { RentalItemId = excavator.Id }));
        Assert.Equal([_waitingOrder.OrderCode], await Codes(new() { ListingId = excavator.EquipmentListingId }));
        Assert.Equal([_lifecycleOrder.OrderCode], await Codes(new() { AssetId = mixers.UnitAssignments.First().AssetId }));
        Assert.Equal([_waitingOrder.OrderCode, _lifecycleOrder.OrderCode], await Codes(new() { SupplierId = _cuong }));
        Assert.Equal([_unpaidOrder.OrderCode], await Codes(new() { RenterId = _dung }));
        Assert.Empty(await Codes(new() { OrderCode = "DH-2026-999999" }));
    }

    [Fact]
    public async Task Search_FiltersByOrderStatusItemStatusAndOrderDate()
    {
        await SeedAsync();
        DateTimeOffset now = OrderTestData.Now;

        Assert.Equal([_unpaidOrder.OrderCode], await Codes(new() { Status = "PENDING_PAYMENT" }));
        Assert.Equal([_lifecycleOrder.OrderCode], await Codes(new() { ItemStatus = "REJECTED" }));
        Assert.Equal(
            [_waitingOrder.OrderCode],
            await Codes(new() { CreatedFrom = now.AddDays(1).ToString("O", CultureInfo.InvariantCulture), CreatedTo = now.AddDays(2).ToString("O", CultureInfo.InvariantCulture) }));
        // Giờ Việt Nam: 07:00+07:00 là 00:00Z, gồm đơn đặt đúng lúc đó.
        Assert.Equal(
            [_unpaidOrder.OrderCode, _waitingOrder.OrderCode],
            await Codes(new() { CreatedFrom = now.AddDays(1).ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) }));
    }

    [Fact]
    public async Task Search_LineFiltersMustMatchTheSameLine()
    {
        await SeedAsync();

        // Đơn vòng đời có dòng của Bình (bị từ chối) và dòng của Cường (chờ quyết toán), nhưng không có
        // dòng nào của Bình chờ quyết toán.
        Assert.Empty(await Codes(new() { SupplierId = _binh, ItemStatus = "PENDING_SETTLEMENT" }));
        Assert.Equal([_lifecycleOrder.OrderCode], await Codes(new() { SupplierId = _cuong, ItemStatus = "PENDING_SETTLEMENT" }));

        RentalItem binhsExcavator = _waitingOrder.Items.Single(i => i.SupplierId == _binh);
        Assert.Empty(await Codes(new() { SupplierId = _cuong, ListingId = binhsExcavator.EquipmentListingId }));
    }

    [Theory]
    [InlineData("SHIPPED", null, null, "Order.InvalidStatus")]
    [InlineData(null, "2026-10-01T00:00:00", null, "Order.InvalidDateRange")]
    [InlineData(null, "2026-10-01", null, "Order.InvalidDateRange")]
    [InlineData(null, "hôm qua", null, "Order.InvalidDateRange")]
    [InlineData(null, "2026-10-02T00:00:00Z", "2026-10-01T00:00:00Z", "Order.InvalidDateRange")]
    [InlineData(null, "2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z", "Order.InvalidDateRange")]
    public async Task Search_InvalidStatusOrDateRange_IsRejected(string? status, string? from, string? to, string code)
    {
        await SeedAsync();

        Result<PagedResponse<AdminOrderSummaryResponse>> result =
            await Search(new() { Status = status, CreatedFrom = from, CreatedTo = to });

        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public async Task Search_AndDetail_RejectAnonymousServiceAndNonAdminCallers()
    {
        await SeedAsync();
        AdminOrderQueryService service = Service();

        Result<PagedResponse<AdminOrderSummaryResponse>> anonymous =
            await service.SearchOrdersAsync(new(), OrderTestData.Anonymous());
        Result<PagedResponse<AdminOrderSummaryResponse>> serviceToken =
            await service.SearchOrdersAsync(new(), RentalCallerContext.ForService("inventory", roles: ["admin"]));
        Result<PagedResponse<AdminOrderSummaryResponse>> renter =
            await service.SearchOrdersAsync(new(), OrderTestData.User(_an));
        Result<AdminOrderDetailResponse> supplierDetail =
            await service.GetOrderAsync(_waitingOrder.Id, OrderTestData.User(_binh));

        Assert.Equal(ErrorType.Unauthorized, anonymous.Error.Type);
        Assert.Equal("Auth.ServiceTokenNotAllowed", serviceToken.Error.Code);
        Assert.Equal("Auth.Forbidden", renter.Error.Code);
        Assert.Equal("Auth.Forbidden", supplierDetail.Error.Code);
    }

    [Fact]
    public async Task Detail_ShowsInternalReferencesUnitsAndRefunds_WithMaskedAccount()
    {
        await SeedAsync();
        RentalItem rammer = _lifecycleOrder.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa");
        RentalItem excavator = _lifecycleOrder.Items.Single(i => i.ListingTitleSnapshot == "Máy xúc CAT 320");
        RentalItem mixers = _lifecycleOrder.Items.Single(i => i.SupplierId == _cuong);

        Result<AdminOrderDetailResponse> result = await Service().GetOrderAsync(_lifecycleOrder.Id, Admin());

        Assert.True(result.IsSuccess);
        AdminOrderDetailResponse order = result.Value;
        Assert.Equal(_an, order.RenterId);
        Assert.Equal($"INV-{_lifecycleOrder.OrderCode}", order.InvoiceRef);
        Assert.Equal($"PAY-{_lifecycleOrder.OrderCode}", order.PaymentRef);
        Assert.Equal(OrderPaymentState.Paid, order.PaymentState);
        Assert.Equal(new AdminRefundAccountResponse("VCB", "******6789", "NGUYỄN VĂN AN"), order.RefundAccount);
        Assert.Equal([_binh, _cuong], order.SubOrders.Select(s => s.SupplierId));

        List<AdminOrderItemResponse> lines = order.SubOrders.SelectMany(s => s.Items).ToList();
        AdminOrderItemResponse rammerLine = lines.Single(l => l.RentalItemId == rammer.Id);
        Assert.Equal(new OrderItemRefundResponse(OrderRefundState.Completed, "REF-2026-000001"), rammerLine.Refund);
        Assert.Equal(rammer.AllocationGroupId, rammerLine.AllocationGroupId);
        Assert.Equal("Máy đang bảo trì", rammerLine.SupplierDecisionReason);
        Assert.Equal(_binh, rammerLine.SupplierDecisionBy);
        Assert.Equal(OrderRefundState.Processing, lines.Single(l => l.RentalItemId == excavator.Id).Refund.State);

        AdminOrderItemResponse mixerLine = lines.Single(l => l.RentalItemId == mixers.Id);
        Assert.Equal("CONF-1", mixerLine.InventoryConfirmationRef);
        Assert.Equal(1_500_000m, mixerLine.Settlement!.ProposedDeductionAmount);
        Assert.Equal(
            mixers.UnitAssignments.OrderBy(u => u.Id).Select(u => (u.AssetId, u.AllocationId)),
            mixerLine.Units.Select(u => (u.AssetId, u.AllocationId)));
        AdminUnitResponse damaged = mixerLine.Units.Single(u => u.ReturnCondition == UnitCondition.Damaged);
        Assert.Equal("Vỡ thùng trộn", damaged.ReturnNote);
        Assert.Equal(UnitCondition.Normal, damaged.HandoverCondition);

        Assert.DoesNotContain("0123456789", JsonSerializer.Serialize(order), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShowsStateHistoryOfOrderAndLines_OldestFirst()
    {
        await SeedAsync();
        RentalItem rammer = _lifecycleOrder.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa");

        Result<AdminOrderDetailResponse> result = await Service().GetOrderAsync(_lifecycleOrder.Id, Admin());

        IReadOnlyList<AdminStateTransitionResponse> history = result.Value.History;
        Assert.Equal(_lifecycleOrder.StateTransitions.Count, history.Count);
        Assert.Equal(history.OrderBy(t => t.OccurredAt), history);
        Assert.Contains(history, t => t.RentalItemId == null && t.ToState == nameof(RentalOrderStatus.Paid));

        AdminStateTransitionResponse rejected = Assert.Single(
            history,
            t => t.RentalItemId == rammer.Id && t.ToState == nameof(RentalItemStatus.Rejected));
        Assert.Equal(_binh, rejected.ActorId);
        Assert.Equal(ActorRole.Supplier, rejected.ActorRole);
        Assert.Equal(OrderTestData.Now.AddHours(1), rejected.OccurredAt);
    }

    [Fact]
    public async Task Detail_UnknownOrder_IsNotFound()
    {
        await SeedAsync();

        Result<AdminOrderDetailResponse> result = await Service().GetOrderAsync(Guid.NewGuid(), Admin());

        Assert.Equal("Order.NotFound", result.Error.Code);
    }

    private RentalCallerContext Admin() => RentalCallerContext.ForUser(_admin, roles: ["admin"]);

    private AdminOrderQueryService Service() =>
        new(new RentalOrderRepository(NewContext()), NullLogger<AdminOrderQueryService>.Instance);

    private Task<Result<PagedResponse<AdminOrderSummaryResponse>>> Search(AdminOrderSearchRequest request) =>
        Service().SearchOrdersAsync(request, Admin());

    private async Task<List<string>> Codes(AdminOrderSearchRequest request)
    {
        Result<PagedResponse<AdminOrderSummaryResponse>> result = await Search(request);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(result.Value.Items.Count, result.Value.TotalCount);
        return result.Value.Items.Select(o => o.OrderCode).ToList();
    }

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task SeedAsync()
    {
        DateTimeOffset now = OrderTestData.Now;

        _lifecycleOrder = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000010",
                now,
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
            now.AddMinutes(5));

        // Khi load từ database, EF nối dòng với đơn và dòng ghi lịch sử trạng thái vào đơn qua navigation đó.
        OrderTestData.LinkItemsToOrder(_lifecycleOrder);

        foreach (RentalItem rejected in _lifecycleOrder.Items.Where(i => i.SupplierId == _binh))
        {
            rejected.ApplySupplierReject(_binh, now.AddHours(1), "Máy đang bảo trì", null, $"reject-{rejected.Id:N}");
            OrderTestData.RequestRefund(_lifecycleOrder, rejected, now.AddHours(1));
        }

        _lifecycleOrder.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa").RecordRefund("REF-2026-000001");

        RentalItem mixers = _lifecycleOrder.Items.Single(i => i.SupplierId == _cuong);
        mixers.ApplySupplierAccept(_cuong, now.AddHours(1), "CONF-1", "accept-mixers");
        OrderTestData.HandOver(mixers, now.AddDays(3), "Giao tại công trình");

        List<RentalUnitAssignment> units = mixers.UnitAssignments.ToList();
        Result<ReturnRecord> returned = mixers.RecordAssetReturns(
            _cuong,
            now.AddDays(6),
            null,
            [
                new ReturnUnitData(units[0].Id, UnitCondition.Normal, null, []),
                new ReturnUnitData(units[1].Id, UnitCondition.Damaged, "Vỡ thùng trộn", [Guid.NewGuid()])
            ],
            now.AddDays(6),
            _cuong);
        Assert.True(returned.IsSuccess, returned.IsFailure ? returned.Error.Message : null);

        Result<DeductionProposal> proposal = mixers.SubmitDeduction(
            _cuong, 1_500_000m, "Vỡ thùng trộn", [Guid.NewGuid()], [units[1].AssetId], now.AddDays(7));
        Assert.True(proposal.IsSuccess, proposal.IsFailure ? proposal.Error.Message : null);

        _waitingOrder = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000011",
                now.AddDays(1),
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
            now.AddDays(1).AddMinutes(5));

        _unpaidOrder = OrderTestData.Order(
            _dung,
            "DH-2026-000012",
            now.AddDays(2),
            OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m));

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.AddRange(_lifecycleOrder, _waitingOrder, _unpaidOrder);
        await context.SaveChangesAsync();
    }
}
