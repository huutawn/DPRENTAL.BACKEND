using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Nhà cung cấp được định danh bằng JWT sub, giống luồng nhận/từ chối dòng thuê; chỉ thấy đơn con có SupplierId của mình.
/// </summary>
public sealed class SupplierSubOrderQueryService(
    IRentalOrderRepository orders,
    IOrderReviewStatusEnricher reviewEnricher) : ISupplierSubOrderQueryService
{
    public SupplierSubOrderQueryService(IRentalOrderRepository orders)
        : this(orders, DefaultOrderReviewStatusEnricher.Instance)
    {
    }
    // Chỉ đọc suborder thuộc người dùng supplier và đã thanh toán hợp lệ; chuẩn hóa status/phân trang trước truy vấn.
    public async Task<Result<PagedResponse<SupplierSubOrderSummaryResponse>>> GetMySubOrdersAsync(
        int page,
        int pageSize,
        string? status,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = OrderQueryMapper.RequirePersonalCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<RentalItemStatus?> statusFilter = OrderQueryMapper.ParseStatusFilter<RentalItemStatus>(status);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (statusFilter.IsFailure)
        {
            return statusFilter.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<SupplierSubOrderSummaryRecord> records = await orders.GetPaidSubOrderPageForSupplierAsync(
            supplier.Value,
            statusFilter.Value,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        // Dựng trang đơn con supplier từ projection đã giới hạn sở hữu/thanh toán, tóm tắt trạng thái từ các item của họ.
        List<SupplierSubOrderSummaryResponse> items = records.Items
            .Select(r => new SupplierSubOrderSummaryResponse(
                r.SubOrderId,
                r.SubOrderCode,
                r.OrderCode,
                SubOrderStatusPolicy.Summarize(r.ItemStatuses),
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemStatuses.Count,
                r.PaidAt,
                r.CreatedAt,
                r.OrderVersion))
            .ToList();

        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    // Tra cứu suborder thuộc supplier cùng order đã trả tiền; không tìm thấy hoặc ngoài phạm vi đều trả NotFound.
    public async Task<Result<SupplierSubOrderDetailResponse>> GetMySubOrderAsync(
        Guid subOrderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> supplier = OrderQueryMapper.RequirePersonalCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        RentalSubOrder? subOrder = await orders.GetPaidSubOrderForSupplierAsync(subOrderId, supplier.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (subOrder?.RentalOrder is not RentalOrder order)
        {
            return OrderQueryErrors.SubOrderNotFound;
        }

        var pairs = subOrder.Items.Select(i => (order.Id, i.Id));
        var reviewStatuses = await reviewEnricher.FetchReviewStatusesAsync(pairs, cancellationToken);

        // Dựng các dòng thuộc đúng suborder supplier đã được phép đọc, gồm snapshot và mapping unit cần cho bàn giao/trả.
        List<SupplierOrderItemResponse> items = subOrder.Items
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.Id)
            .Select(i => ToItem(i, reviewStatuses.GetValueOrDefault((order.Id, i.Id), ReviewStatusDto.Unknown)))
            .ToList();
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new SupplierSubOrderDetailResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            order.OrderCode,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            order.Currency,
            order.PaymentConfirmedAt,
            subOrder.CreatedAt,
            OrderQueryMapper.ToDelivery(order),
            rental,
            deposit,
            rental + deposit,
            items,
            order.Version);
    }

    // Chiếu item cho supplier gồm mapping unit và bằng chứng cần bàn giao/trả máy, dùng snapshot thương mại đã chốt.
    private static SupplierOrderItemResponse ToItem(RentalItem item, ReviewStatusDto reviewStatus)
    {
        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        OrderItemResponse basics = OrderQueryMapper.ToItem(item, isSupplier: true);

        return new SupplierOrderItemResponse(
            basics.Name,
            basics.Quantity,
            basics.StartDate,
            basics.EndDate,
            item.Id,
            item.EquipmentListingId,
            item.Status,
            item.RentalDays,
            item.DailyRateSnapshot,
            item.DepositPerUnitSnapshot,
            item.RentalAmount,
            item.DepositAmount,
            item.SupplierConfirmationDeadline,
            item.SupplierDecisionAt,
            OrderQueryMapper.ToHandover(item),
            OrderQueryMapper.ToReturns(item),
            OrderQueryMapper.ToSettlement(item),
            item.UnitAssignments
                .OrderBy(u => u.Id)
                .Select(u => new SupplierUnitResponse(u.Id, u.AssetId, u.Status, u.ReturnCondition))
                .ToList(),
            OrderQueryMapper.ToReturnRequests(item),
            basics.PickupLocation,
            reviewStatus);
    }
}
