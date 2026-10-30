using System.Text.Json;
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

/// <remarks>
/// Luồng đọc đơn renter: xác định caller → chuẩn hóa bộ lọc/phân trang → truy vấn theo quyền sở hữu → dựng response.
/// Trạng thái và quyền thao tác trả cho giao diện là ảnh chụp tại lần đọc; lệnh thay đổi vẫn kiểm tra lại.
/// </remarks>
public sealed class RenterOrderQueryService(IRentalOrderRepository orders) : IRenterOrderQueryService
{
    // Lọc theo danh tính renter, trạng thái và refund; phân trang tại repository rồi ghép quyền hủy cùng tiến độ hoàn tiền.
    public async Task<Result<PagedResponse<RenterOrderSummaryResponse>>> GetMyOrdersAsync(
        int page,
        int pageSize,
        string? status,
        IRentalCallerContext caller,
        string? refundState = null,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> renter = OrderQueryMapper.RequirePersonalCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<RentalOrderStatus?> statusFilter = OrderQueryMapper.ParseStatusFilter<RentalOrderStatus>(status);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (statusFilter.IsFailure)
        {
            return statusFilter.Error;
        }

        // Parse tiêu chí hoàn tiền thành bộ lọc repository; phân biệt khoản đang chờ với đã có xác nhận chuyển tiền.
        Result<bool?> refundedFilter = ParseRefundFilter(refundState);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (refundedFilter.IsFailure)
        {
            return refundedFilter.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<RenterOrderSummaryRecord> records = await orders.GetRenterOrderPageAsync(
            renter.Value,
            statusFilter.Value,
            refundedFilter.Value,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        // Dựng danh sách renter từ projection đã lọc sở hữu; ghép tiến độ refund và quyền hủy bằng cùng policy của lệnh.
        List<RenterOrderSummaryResponse> items = records.Items
            .Select(r => new RenterOrderSummaryResponse(
                r.OrderId,
                r.OrderCode,
                r.Status,
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemCount,
                r.SubOrderCount,
                r.PaymentDeadline,
                r.CreatedAt,
                r.Items.Select(OrderQueryMapper.ToSummaryItem).ToList(),
                r.Version,
                OrderQueryMapper.ToRefundProgress(r.RefundCount, r.PendingRefundCount),
                r.RefundedAmount,
                RenterOrderCancellationPolicy.CanCancel(r.Status, r.Items.Select(i => i.Status))))
            .ToList();

        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    // Đọc order trong phạm vi renter và kiểm tra quyền trước khi dựng snapshot, suborder, tiền và khả năng hủy.
    public async Task<Result<RenterOrderDetailResponse>> GetMyOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> renter = OrderQueryMapper.RequirePersonalCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        RentalOrder? order = await orders.GetRenterOrderWithSubOrdersAsync(orderId, renter.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        // Query đã lọc theo người thuê; policy chặn thêm một lớp nếu query sau này bị đổi.
        Result access = RentalAuthorizationPolicy.AuthorizeOrderRead(caller, order);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (access.IsFailure)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        Dictionary<Guid, RentalRefundLine> refundLines = OrderQueryMapper.RefundLinesByItem(order);

        // Sắp đơn con theo mã và dựng chi tiết cho renter; số tiền tổng order được cộng từ đúng tập đơn con này.
        List<RenterSubOrderResponse> subOrders = order.SubOrders
            .OrderBy(s => s.SubOrderCode, StringComparer.Ordinal)
            .Select(s => ToSubOrder(s, refundLines))
            .ToList();
        decimal totalRental = subOrders.Sum(s => s.TotalRentalAmount);
        decimal totalDeposit = subOrders.Sum(s => s.TotalDepositAmount);

        return new RenterOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentConfirmedAt,
            OrderQueryMapper.ToPaymentState(order),
            OrderQueryMapper.ToDelivery(order),
            OrderQueryMapper.ParseRefundAccount(order.RefundDestinationRef),
            totalRental,
            totalDeposit,
            totalRental + totalDeposit,
            subOrders,
            order.Version,
            OrderQueryMapper.ToRefundProgress(refundLines.Count, refundLines.Values.Count(l => l.Confirmation is null)),
            refundLines.Values.Sum(l => l.Confirmation?.RefundAmount ?? 0m),
            RenterOrderCancellationPolicy.CanCancel(order.Status, order.SubOrders.SelectMany(s => s.Items).Select(i => i.Status)));
    }

    // Tổng hợp tiền và trạng thái từ các item thuộc một supplier, đồng thời giữ thứ tự hiển thị ổn định.
    private static RenterSubOrderResponse ToSubOrder(RentalSubOrder subOrder, Dictionary<Guid, RentalRefundLine> refundLines)
    {
        // Sắp item theo ngày/ID trong đơn con để các lần tải lại giữ thứ tự ổn định và ghép refund theo đúng item.
        List<RenterOrderItemResponse> items = subOrder.Items
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.Id)
            .Select(i => ToItem(i, refundLines.GetValueOrDefault(i.Id)))
            .ToList();
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new RenterSubOrderResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            subOrder.SupplierId,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            rental,
            deposit,
            rental + deposit,
            items);
    }

    // Dựng chi tiết item cho renter bằng snapshot thương mại và các mapper handover/return/settlement dùng chung.
    private static RenterOrderItemResponse ToItem(RentalItem item, RentalRefundLine? refundLine)
    {
        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        OrderItemResponse basics = OrderQueryMapper.ToItem(item);

        return new RenterOrderItemResponse(
            basics.Name,
            basics.Quantity,
            basics.StartDate,
            basics.EndDate,
            item.Id,
            item.EquipmentListingId,
            item.Status,
            new OrderListingSnapshotResponse(
                item.ListingDescriptionSnapshot,
                item.ThumbnailMediaIdSnapshot,
                item.EquipmentTypeNameSnapshot,
                item.BrandNameSnapshot,
                item.ModelSnapshot,
                ToSpecValues(item.SpecValuesSnapshot),
                basics.PickupLocation,
                item.EquipmentTypeId,
                item.BrandId,
                item.SpecSchemaVersionSnapshot),
            item.RentalDays,
            item.DailyRateSnapshot,
            item.DepositPerUnitSnapshot,
            item.RentalAmount,
            item.DepositAmount,
            item.SupplierConfirmationDeadline,
            item.SupplierDecisionAt,
            item.Status == RentalItemStatus.Rejected ? item.SupplierDecisionReason : null,
            OrderQueryMapper.ToRefund(item, refundLine),
            OrderQueryMapper.ToHandover(item),
            OrderQueryMapper.ToReturns(item),
            OrderQueryMapper.ToSettlement(item),
            OrderQueryMapper.ToRenterUnits(item),
            OrderQueryMapper.ToReturnRequests(item));
    }

    /// <summary>REFUNDED: mọi khoản đã chuyển khoản; PENDING: còn khoản chờ hoàn; rỗng là không lọc. NONE hay giá trị lạ là lỗi.</summary>
    private static Result<bool?> ParseRefundFilter(string? refundState)
    {
        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<OrderRefundProgress?> progress = OrderQueryMapper.ParseStatusFilter<OrderRefundProgress>(refundState);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (progress.IsFailure || progress.Value == OrderRefundProgress.None)
        {
            return OrderQueryErrors.InvalidRefundState;
        }

        return Result.Success<bool?>(progress.Value is { } p ? p == OrderRefundProgress.Refunded : null);
    }

    /// <summary>Thông số được lưu dạng chuỗi JSON; trả ra dạng object. Chuỗi hỏng thì bỏ qua thay vì làm lỗi cả đơn.</summary>
    private static JsonElement? ToSpecValues(string? specValuesJson)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(specValuesJson))
        {
            return null;
        }

        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            using JsonDocument document = JsonDocument.Parse(specValuesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException)
        {
            return null;
        }
    }
}
