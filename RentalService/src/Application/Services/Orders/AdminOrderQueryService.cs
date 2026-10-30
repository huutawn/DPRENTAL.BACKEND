using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Refunds;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Admin xem được mọi đơn; tài khoản hoàn tiền ở phần đơn bị che, riêng mục Hoàn tiền hiện đủ số để Admin chuyển khoản.
/// Mỗi lần xem chi tiết đều được ghi log.
/// </summary>
public sealed partial class AdminOrderQueryService(
    IRentalOrderRepository orders,
    ILogger<AdminOrderQueryService> logger) : IAdminOrderQueryService
{
    private const int VisibleAccountDigits = 4;

    // Chỉ cho phép admin, chuẩn hóa bộ lọc/phân trang rồi đọc projection order tại repository và dựng trang kết quả.
    public async Task<Result<PagedResponse<AdminOrderSummaryResponse>>> SearchOrdersAsync(
        AdminOrderSearchRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(request);

        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result access = RentalAuthorizationPolicy.AuthorizeAdminOrderRead(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (access.IsFailure)
        {
            return access.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<AdminOrderSearchCriteria> criteria = ToCriteria(request);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (criteria.IsFailure)
        {
            return criteria.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        (int page, int pageSize) = OrderQueryMapper.NormalizePaging(request.Page, request.PageSize);
        RecordPage<AdminOrderSummaryRecord> records = await orders.SearchOrdersForAdminAsync(
            criteria.Value,
            (page - 1) * pageSize,
            pageSize,
            cancellationToken);

        // Dựng trang tóm tắt từ projection repository, dùng tổng tiền snapshot của order thay vì tải lại giá listing hiện tại.
        List<AdminOrderSummaryResponse> items = records.Items
            .Select(r => new AdminOrderSummaryResponse(
                r.OrderId,
                r.OrderCode,
                r.Status,
                r.RenterId,
                r.SupplierIds,
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemCount,
                r.InvoiceRef,
                r.PaidAt,
                r.CreatedAt,
                r.Items.Select(OrderQueryMapper.ToSummaryItem).ToList()))
            .ToList();

        return OrderQueryMapper.ToPage(items, page, pageSize, records.TotalCount);
    }

    // Kiểm tra quyền admin trước khi đọc chi tiết, ghi audit truy cập và dựng dữ liệu suborder/lịch sử/hoàn tiền.
    public async Task<Result<AdminOrderDetailResponse>> GetOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result access = RentalAuthorizationPolicy.AuthorizeAdminOrderRead(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (access.IsFailure)
        {
            return access.Error;
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        RentalOrder? order = await orders.GetOrderForAdminAsync(orderId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        // Đơn chứa dữ liệu cá nhân của người thuê: ghi lại ai đã xem.
        LogAdminViewedOrder(logger, caller.UserId, order.Id, order.OrderCode);

        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        Dictionary<Guid, RentalRefundLine> refundLines = OrderQueryMapper.RefundLinesByItem(order);
        List<AdminSubOrderResponse> subOrders = order.SubOrders
            .OrderBy(s => s.SubOrderCode, StringComparer.Ordinal)
            .Select(s => ToSubOrder(s, refundLines))
            .ToList();
        decimal totalRental = subOrders.Sum(s => s.TotalRentalAmount);
        decimal totalDeposit = subOrders.Sum(s => s.TotalDepositAmount);

        // Sắp lịch sử thao tác theo mốc/ID để admin đối soát trình tự; không suy ngược lịch sử từ trạng thái cuối của order.
        List<AdminStateTransitionResponse> history = order.StateTransitions
            .OrderBy(t => t.OccurredAt)
            .ThenBy(t => t.Id)
            .Select(t => new AdminStateTransitionResponse(
                t.RentalItemId,
                t.FromState,
                t.ToState,
                t.Action,
                t.ActorId,
                t.ActorRole,
                t.Reason,
                t.OccurredAt))
            .ToList();

        return new AdminOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.RenterId,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentRef,
            order.PaymentConfirmedAt,
            OrderQueryMapper.ToPaymentState(order),
            OrderQueryMapper.ToDelivery(order),
            OrderQueryMapper.ParseRefundAccount(order.RefundDestinationRef) is { } account
                ? new AdminRefundAccountResponse(
                    account.BankCode,
                    MaskAccountNumber(account.AccountNumber),
                    account.AccountHolderName)
                : null,
            totalRental,
            totalDeposit,
            totalRental + totalDeposit,
            subOrders,
            history,
            order.Version,
            refundLines.Values
                .OrderBy(l => l.Request.CreatedAt)
                .ThenBy(l => l.Request.Id)
                .Select(l => AdminRefundMapper.ToOrderRefund(order, l))
                .ToList());
    }

    // Validate bộ lọc mã đơn, ID, trạng thái và khoảng thời gian trước khi gửi tiêu chí cụ thể cho repository.
    private static Result<AdminOrderSearchCriteria> ToCriteria(AdminOrderSearchRequest request)
    {
        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<RentalOrderStatus?> status = OrderQueryMapper.ParseStatusFilter<RentalOrderStatus>(request.Status);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (status.IsFailure)
        {
            return status.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<RentalItemStatus?> itemStatus = OrderQueryMapper.ParseStatusFilter<RentalItemStatus>(request.ItemStatus);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (itemStatus.IsFailure)
        {
            return itemStatus.Error;
        }

        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!TryParseInstant(request.CreatedFrom, out DateTimeOffset? from) ||
            !TryParseInstant(request.CreatedTo, out DateTimeOffset? to) ||
            (from.HasValue && to.HasValue && from.Value >= to.Value))
        {
            return OrderQueryErrors.InvalidDateRange;
        }

        return new AdminOrderSearchCriteria(
            NormalizeCode(request.OrderCode),
            NormalizeCode(request.SubOrderCode),
            request.RentalItemId,
            request.ListingId,
            request.AssetId,
            request.SupplierId,
            request.RenterId,
            status.Value,
            itemStatus.Value,
            from,
            to);
    }

    /// <summary>Mã đơn luôn viết hoa (DH-2026-000001); bỏ khoảng trắng và chấp nhận chữ thường.</summary>
    private static string? NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>Rỗng là không lọc. Bắt buộc có Z hoặc ±HH:mm để không phụ thuộc múi giờ của máy chủ.</summary>
    private static bool TryParseInstant(string? value, out DateTimeOffset? instant)
    {
        // Khởi tạo đầu ra parse chưa hợp lệ; chỉ gán giá trị có thể dùng khi toàn bộ bước parse đã thành công.
        instant = null;
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string trimmed = value.Trim();
        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!IsoInstantRegex().IsMatch(trimmed) ||
            !DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return false;
        }

        // Chuẩn hóa thời điểm đã parse về UTC, tránh so sánh phụ thuộc offset hoặc timezone của server.
        instant = parsed.ToUniversalTime();
        return true;
    }

    // Che số tài khoản trước khi trả thông tin hoàn tiền trong màn hình tra cứu admin.
    private static string MaskAccountNumber(string accountNumber) =>
        accountNumber.Length <= VisibleAccountDigits
            ? new string('*', accountNumber.Length)
            : new string('*', accountNumber.Length - VisibleAccountDigits) + accountNumber[^VisibleAccountDigits..];

    // Dựng chi tiết đơn con từ đúng các item của supplier và ghép tiến độ hoàn tiền tương ứng.
    private static AdminSubOrderResponse ToSubOrder(RentalSubOrder subOrder, Dictionary<Guid, RentalRefundLine> refundLines)
    {
        // Dựng item của một suborder cùng khoản refund tương ứng để phần tiền của các supplier không bị trộn.
        List<AdminOrderItemResponse> items = subOrder.Items
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.Id)
            .Select(i => ToItem(i, refundLines.GetValueOrDefault(i.Id)))
            .ToList();
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new AdminSubOrderResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            subOrder.SupplierId,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            rental,
            deposit,
            rental + deposit,
            items);
    }

    // Chiếu chi tiết item cho admin, gồm snapshot và chứng từ cần đối soát, không truy vấn service ngoài để ghi đè lịch sử.
    private static AdminOrderItemResponse ToItem(RentalItem item, RentalRefundLine? refundLine)
    {
        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        OrderItemResponse basics = OrderQueryMapper.ToItem(item);

        return new AdminOrderItemResponse(
            basics.Name,
            basics.Quantity,
            basics.StartDate,
            basics.EndDate,
            item.Id,
            item.EquipmentListingId,
            item.SupplierId,
            item.Status,
            item.RentalDays,
            item.DailyRateSnapshot,
            item.DepositPerUnitSnapshot,
            item.RentalAmount,
            item.DepositAmount,
            item.AllocationGroupId,
            item.InventoryConfirmationRef,
            item.SupplierConfirmationDeadline,
            item.SupplierDecisionAt,
            item.SupplierDecisionBy,
            item.SupplierDecisionReason,
            item.SupplierDecisionNote,
            OrderQueryMapper.ToRefund(item, refundLine),
            item.SettlementRef,
            OrderQueryMapper.ToHandover(item),
            OrderQueryMapper.ToReturns(item),
            OrderQueryMapper.ToSettlement(item),
            item.UnitAssignments
                .OrderBy(u => u.Id)
                .Select(u => new AdminUnitResponse(
                    u.Id,
                    u.AssetId,
                    u.AllocationId,
                    u.Status,
                    u.HandoverCondition,
                    u.ReturnCondition,
                    u.ReturnNote,
                    u.InventoryDispositionRef))
                .ToList());
    }

    // Ghi audit có cấu trúc cho lần admin xem đơn; không đưa toàn bộ payload đơn vào log.
    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} viewed rental order {OrderId} ({OrderCode})")]
    private static partial void LogAdminViewedOrder(ILogger logger, Guid? adminId, Guid orderId, string orderCode);

    // Sinh regex kiểm tra định dạng timestamp có timezone cho bộ lọc admin.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex IsoInstantRegex();
}
