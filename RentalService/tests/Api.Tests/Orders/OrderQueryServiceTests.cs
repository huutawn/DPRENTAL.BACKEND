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
            await RenterService().GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an));

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
            await RenterService().GetMyOrdersAsync(2, 2, null, OrderTestData.User(_an));
        Result<PagedResponse<RenterOrderSummaryResponse>> clamped =
            await RenterService().GetMyOrdersAsync(0, 500, null, OrderTestData.User(_an));

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

        // Dòng máy lấy từ bản chụp lúc đặt hàng, ngày dùng cuối có tính.
        RenterOrderItemResponse excavator = Assert.Single(binh.Items);
        RentalItem excavatorItem = _paidMixedOrder.Items.Single(i => i.SupplierId == _binh);
        Assert.Equal("Máy xúc CAT 320", excavator.Name);
        Assert.Equal(excavatorItem.Id, excavator.RentalItemId);
        Assert.Equal(excavatorItem.EquipmentListingId, excavator.EquipmentListingId);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, excavator.Status);
        Assert.Equal(new DateOnly(2026, 10, 12), excavator.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 14), excavator.EndDate);
        Assert.Equal(3, excavator.RentalDays);
        Assert.Equal(3_000_000m, excavator.DailyRate);
        Assert.Equal(20_000_000m, excavator.DepositPerUnit);
        Assert.Equal(9_000_000m, excavator.RentalAmount);
        Assert.Equal(20_000_000m, excavator.DepositAmount);
        Assert.Equal(excavatorItem.SupplierConfirmationDeadline, excavator.SupplierConfirmationDeadline);
        Assert.Equal("Máy xúc", excavator.Listing.EquipmentTypeName);
        Assert.Equal(excavatorItem.EquipmentTypeId, excavator.Listing.EquipmentTypeId);
        Assert.Equal(excavatorItem.BrandId, excavator.Listing.BrandId);
        Assert.Equal(excavatorItem.SpecSchemaVersionSnapshot, excavator.Listing.SpecSchemaVersion);
        Assert.Equal("Caterpillar", excavator.Listing.BrandName);
        Assert.Null(excavator.Listing.PickupLocation);
        Assert.Equal(excavatorItem.ThumbnailMediaIdSnapshot, excavator.Listing.ThumbnailMediaId);
        Assert.Equal(55, excavator.Listing.SpecValues!.Value.GetProperty("cong_suat_kw").GetInt32());
        Assert.Equal(new OrderItemRefundResponse(OrderRefundState.None, null), excavator.Refund);
        Assert.Null(excavator.Handover);
        Assert.Empty(excavator.Returns);
        Assert.Null(excavator.Settlement);

        RenterSubOrderResponse cuong = order.SubOrders[1];
        Assert.Equal(_cuong, cuong.SupplierId);
        Assert.Equal(2, Assert.Single(cuong.Items).Quantity);

        Assert.Equal(OrderPaymentState.Paid, order.PaymentState);
        Assert.Equal(new RenterRefundAccountResponse("VCB", "0123456789", "NGUYỄN VĂN AN"), order.RefundAccount);
    }

    [Fact]
    public async Task Renter_OrderDetail_NeverExposesPhysicalAssetsOrInventoryReferences()
    {
        await SeedAsync();

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(_paidMixedOrder.Id, OrderTestData.User(_an));

        string json = JsonSerializer.Serialize(result.Value, WebJson);
        foreach (RentalItem item in _paidMixedOrder.Items)
        {
            Assert.DoesNotContain(item.AllocationGroupId, json, StringComparison.Ordinal);
            foreach (RentalUnitAssignment unit in item.UnitAssignments)
            {
                Assert.DoesNotContain(unit.AssetId.ToString(), json, StringComparison.Ordinal);
                Assert.DoesNotContain(unit.AllocationId.ToString(), json, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("assetId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allocation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"PAY-{_paidMixedOrder.OrderCode}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renter_OrderDetail_ShowsPaymentStateOfUnpaidAndLatePaidOrders()
    {
        await SeedAsync();

        Result<RenterOrderDetailResponse> unpaid =
            await RenterService().GetMyOrderAsync(_unpaidOrder.Id, OrderTestData.User(_an));
        Result<RenterOrderDetailResponse> latePaid =
            await RenterService().GetMyOrderAsync(_latePaidOrder.Id, OrderTestData.User(_an));

        Assert.Equal(OrderPaymentState.Pending, unpaid.Value.PaymentState);
        Assert.Null(unpaid.Value.PaidAt);
        Assert.Equal(OrderPaymentState.LatePaymentRefund, latePaid.Value.PaymentState);
    }

    [Fact]
    public async Task Renter_OrderDetail_OrderSavedBeforeRefundAccountsWereStored_HasNoRefundAccount()
    {
        RentalOrder legacy = OrderTestData.Order(
            _an,
            "DH-2026-000009",
            OrderTestData.Now,
            OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m));
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.RefundDestinationRef))!.SetValue(legacy, "ORIGINAL_PAYMENT_SOURCE");
        await using (RentalServiceDbContext context = NewContext())
        {
            context.RentalOrders.Add(legacy);
            await context.SaveChangesAsync();
        }

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(legacy.Id, OrderTestData.User(_an));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.RefundAccount);
    }

    [Fact]
    public async Task Renter_OrderDetail_ShowsRefundProgressPerItem()
    {
        RentalOrder order = await SeedLifecycleOrderAsync();
        RentalItem excavator = order.Items.Single(i => i.ListingTitleSnapshot == "Máy xúc CAT 320");
        RentalItem rammer = order.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa");

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(order.Id, OrderTestData.User(_an));

        Assert.True(result.IsSuccess);
        List<RenterOrderItemResponse> items = result.Value.SubOrders.SelectMany(s => s.Items).ToList();
        RenterOrderItemResponse excavatorLine = items.Single(i => i.RentalItemId == excavator.Id);
        RenterOrderItemResponse rammerLine = items.Single(i => i.RentalItemId == rammer.Id);

        Assert.Equal(RentalItemStatus.Rejected, excavatorLine.Status);
        Assert.Equal("Máy đang bảo trì", excavatorLine.RejectReason);
        Assert.Equal(new OrderItemRefundResponse(OrderRefundState.Processing, null), excavatorLine.Refund);
        Assert.Equal(new OrderItemRefundResponse(OrderRefundState.Completed, "REF-2026-000001"), rammerLine.Refund);
    }

    [Fact]
    public async Task Renter_OrderDetail_ShowsHandoverReturnsAndDepositSettlement()
    {
        RentalOrder order = await SeedLifecycleOrderAsync();
        RentalItem mixers = order.Items.Single(i => i.SupplierId == _cuong);

        Result<RenterOrderDetailResponse> result =
            await RenterService().GetMyOrderAsync(order.Id, OrderTestData.User(_an));

        RenterOrderItemResponse line = result.Value.SubOrders.SelectMany(s => s.Items).Single(i => i.RentalItemId == mixers.Id);
        Assert.Equal(RentalItemStatus.PendingSettlement, line.Status);
        Assert.Equal(
            new OrderHandoverResponse(OrderTestData.Now.AddDays(3), "Giao tại công trình", null),
            line.Handover);

        // Một máy trộn trả bình thường, một máy vỡ thùng.
        OrderReturnResponse returned = Assert.Single(line.Returns);
        Assert.Equal(OrderTestData.Now.AddDays(6), returned.ActualReturnAt);
        Assert.Equal(1, returned.NormalUnits);
        Assert.Equal(1, returned.DamagedUnits);

        OrderSettlementResponse settlement = line.Settlement!;
        Assert.Equal(OrderTestData.Now.AddDays(6), settlement.ReturnsCompletedAt);
        Assert.Equal(OrderTestData.Now.AddDays(8), settlement.DeductionDeadline);
        Assert.Equal(DeductionProposalStatus.Submitted, settlement.DeductionStatus);
        Assert.Equal(1_500_000m, settlement.ProposedDeductionAmount);
        Assert.Equal(OrderTestData.Now.AddDays(9), settlement.RenterResponseDeadline);
        Assert.Null(settlement.FinalDeductionAmount);
        Assert.Null(settlement.DepositRefundAmount);
        Assert.Null(settlement.SettledAt);
    }

    [Fact]
    public async Task Renter_ListFiltersByStatus_InTheSpellingTheApiReturns()
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> paid =
            await RenterService().GetMyOrdersAsync(1, 20, "PAID", OrderTestData.User(_an));
        Result<PagedResponse<RenterOrderSummaryResponse>> pending =
            await RenterService().GetMyOrdersAsync(1, 20, "pending_payment", OrderTestData.User(_an));
        Result<PagedResponse<RenterOrderSummaryResponse>> expired =
            await RenterService().GetMyOrdersAsync(1, 20, "Expired", OrderTestData.User(_an));

        Assert.Equal([_paidMixedOrder.OrderCode], paid.Value.Items.Select(o => o.OrderCode));
        Assert.Equal([_unpaidOrder.OrderCode], pending.Value.Items.Select(o => o.OrderCode));
        Assert.Equal(1, pending.Value.TotalCount);
        // Đơn trả trễ bị đóng thành EXPIRED và chờ hoàn toàn bộ.
        Assert.Equal([_latePaidOrder.OrderCode], expired.Value.Items.Select(o => o.OrderCode));
    }

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("9")]
    [InlineData("PAID;DROP")]
    public async Task Renter_ListWithUnknownStatus_IsRejected(string status)
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> result =
            await RenterService().GetMyOrdersAsync(1, 20, status, OrderTestData.User(_an));

        Assert.Equal("Order.InvalidStatus", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
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
            await SupplierService().GetMySubOrdersAsync(1, 20, null, OrderTestData.User(_binh));

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
    public async Task Supplier_ListFiltersByItemStatus_ForEachWorkQueue()
    {
        await SeedAsync();
        RentalOrder lifecycle = await SeedLifecycleOrderAsync();

        async Task<List<string>> Queue(Guid supplierId, string status)
        {
            Result<PagedResponse<SupplierSubOrderSummaryResponse>> result =
                await SupplierService().GetMySubOrdersAsync(1, 20, status, OrderTestData.User(supplierId));
            Assert.True(result.IsSuccess);
            Assert.Equal(result.Value.Items.Count, result.Value.TotalCount);
            return result.Value.Items.Select(s => s.SubOrderCode).ToList();
        }

        Assert.Equal([$"{_paidMixedOrder.OrderCode}-02"], await Queue(_cuong, "PENDING_SUPPLIER_CONFIRMATION"));
        Assert.Equal([$"{lifecycle.OrderCode}-02"], await Queue(_cuong, "PENDING_SETTLEMENT"));
        Assert.Empty(await Queue(_cuong, "IN_RENTAL"));
        Assert.Empty(await Queue(_cuong, "COMPLETED"));
        Assert.Equal([$"{lifecycle.OrderCode}-01"], await Queue(_binh, "rejected"));
        Assert.Equal(
            [$"{_otherRentersOrder.OrderCode}-01", $"{_paidMixedOrder.OrderCode}-01"],
            await Queue(_binh, "PendingSupplierConfirmation"));
    }

    [Theory]
    [InlineData("SHIPPED")]
    [InlineData("5")]
    [InlineData("IN_RENTAL,COMPLETED")]
    public async Task Supplier_ListWithUnknownStatus_IsRejected(string status)
    {
        await SeedAsync();

        Result<PagedResponse<SupplierSubOrderSummaryResponse>> result =
            await SupplierService().GetMySubOrdersAsync(1, 20, status, OrderTestData.User(_cuong));

        Assert.Equal("Order.InvalidStatus", result.Error.Code);
    }

    [Fact]
    public async Task Supplier_SubOrderDetail_ShowsOwnUnitsHandoverReturnsAndSettlement()
    {
        RentalOrder order = await SeedLifecycleOrderAsync();
        RentalItem mixers = order.Items.Single(i => i.SupplierId == _cuong);
        List<RentalUnitAssignment> assigned = mixers.UnitAssignments.OrderBy(u => u.Id).ToList();

        Result<SupplierSubOrderDetailResponse> result = await SupplierService().GetMySubOrderAsync(
            order.SubOrders.Single(s => s.SupplierId == _cuong).Id, OrderTestData.User(_cuong));

        Assert.True(result.IsSuccess);
        SupplierOrderItemResponse line = Assert.Single(result.Value.Items);
        Assert.Equal(mixers.Id, line.RentalItemId);
        Assert.Equal(RentalItemStatus.PendingSettlement, line.Status);
        Assert.Equal(400_000m, line.DailyRate);
        Assert.Equal(3_000_000m, line.DepositPerUnit);
        Assert.Equal(6_000_000m, line.DepositAmount);
        Assert.Equal(OrderTestData.Now.AddDays(3), line.Handover!.ActualHandoverAt);

        OrderReturnResponse returned = Assert.Single(line.Returns);
        Assert.Equal(1, returned.NormalUnits);
        Assert.Equal(1, returned.DamagedUnits);
        Assert.Equal(1_500_000m, line.Settlement!.ProposedDeductionAmount);

        // Nhà cung cấp cần assignmentId để ghi nhận trả máy và assetId để đề xuất khấu trừ.
        Assert.Equal(
            assigned.Select(u => new SupplierUnitResponse(u.Id, u.AssetId, u.Status, u.ReturnCondition)),
            line.Units);
        Assert.Equal(
            [UnitCondition.Normal, UnitCondition.Damaged],
            line.Units.Select(u => u.ReturnCondition!.Value).Order());
    }

    [Fact]
    public async Task Supplier_SubOrderDetail_NeverExposesRefundsOrOtherSuppliersEquipment()
    {
        RentalOrder order = await SeedLifecycleOrderAsync();

        Result<SupplierSubOrderDetailResponse> cuongs = await SupplierService().GetMySubOrderAsync(
            order.SubOrders.Single(s => s.SupplierId == _cuong).Id, OrderTestData.User(_cuong));
        Result<SupplierSubOrderDetailResponse> binhs = await SupplierService().GetMySubOrderAsync(
            order.SubOrders.Single(s => s.SupplierId == _binh).Id, OrderTestData.User(_binh));

        string cuongsJson = JsonSerializer.Serialize(cuongs.Value, WebJson);
        Assert.DoesNotContain("Máy xúc", cuongsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789", cuongsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("refundRef", cuongsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refundAccount", cuongsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allocation", cuongsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paymentRef", cuongsJson, StringComparison.OrdinalIgnoreCase);
        foreach (Guid asset in order.Items.Where(i => i.SupplierId == _binh).SelectMany(i => i.UnitAssignments).Select(u => u.AssetId))
        {
            Assert.DoesNotContain(asset.ToString(), cuongsJson, StringComparison.OrdinalIgnoreCase);
        }

        // Bình đã hoàn tiền máy đầm cho người thuê nhưng không thấy mã hoàn.
        string binhsJson = JsonSerializer.Serialize(binhs.Value, WebJson);
        Assert.DoesNotContain("REF-2026-000001", binhsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Máy trộn", binhsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queries_RejectAnonymousAndServiceCallers()
    {
        await SeedAsync();

        Result<PagedResponse<RenterOrderSummaryResponse>> anonymous =
            await RenterService().GetMyOrdersAsync(1, 20, null, OrderTestData.Anonymous());
        Result<PagedResponse<SupplierSubOrderSummaryResponse>> service =
            await SupplierService().GetMySubOrdersAsync(1, 20, null, OrderTestData.Service());

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
        Assert.Null(beforeConfirm.Value.SubOrders[0].Items[0].Listing.PickupLocation);

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
        Assert.Equal("Kho Thủ Đức, TP.HCM", afterConfirm.Value.SubOrders[0].Items[0].Listing.PickupLocation);
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

    /// <summary>
    /// Đơn đã thanh toán của An: Bình từ chối máy xúc (đang hoàn tiền) và máy đầm (đã hoàn xong);
    /// Cường giao 2 máy trộn, nhận lại một máy vỡ thùng và đề xuất khấu trừ 1.500.000.
    /// </summary>
    private async Task<RentalOrder> SeedLifecycleOrderAsync()
    {
        DateTimeOffset now = OrderTestData.Now;
        RentalOrder order = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000010",
                now,
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
            now.AddMinutes(5));

        foreach (RentalItem rejected in order.Items.Where(i => i.SupplierId == _binh))
        {
            rejected.ApplySupplierReject(_binh, now.AddHours(1), "Máy đang bảo trì", null, $"reject-{rejected.Id:N}");
            OrderTestData.RequestRefund(order, rejected, now.AddHours(1));
        }

        order.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa").RecordRefund("REF-2026-000001");

        RentalItem mixers = order.Items.Single(i => i.SupplierId == _cuong);
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

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

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
