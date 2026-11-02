using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.IServices.Refunds;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Refunds;

/// <summary>
/// Đơn yêu cầu hoàn tiền: các khoản theo <see cref="RentalRefundPolicy"/>, kèm tài khoản người thuê khai lúc đặt hàng.
/// Khoản chuyển sang đã hoàn khi Admin xác nhận chuyển khoản. Danh sách hiện đủ số tài khoản nên mỗi lần xem đều được ghi log.
/// </summary>
public sealed partial class AdminRefundQueryService(
    IRentalRefundRepository refunds,
    ILogger<AdminRefundQueryService> logger) : IAdminRefundQueryService
{
    // Kiểm tra quyền admin, validate trạng thái hàng đợi và phân trang tại repository; ghi audit lần xem danh sách.
    public async Task<Result<PagedResponse<AdminRefundQueueItemResponse>>> GetRefundsAsync(
        string? status,
        int page,
        int pageSize,
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

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        Result<AdminRefundQueueState?> state = OrderQueryMapper.ParseStatusFilter<AdminRefundQueueState>(status);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (state.IsFailure)
        {
            return state.Error;
        }

        // Chuẩn hóa bộ lọc và phân trang trước khi gọi repository để phạm vi truy vấn và kích thước response được xác định rõ.
        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<RefundQueueRecord> records = await refunds.GetQueuePageAsync(
            state.Value is { } s ? s == AdminRefundQueueState.Completed : null,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        // Danh sách hiện đủ số tài khoản ngân hàng của người thuê: ghi lại ai đã xem.
        LogAdminViewedRefunds(logger, caller.UserId, status, normalizedPage, records.Items.Count);

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        List<AdminRefundQueueItemResponse> items = records.Items.Select(ToResponse).ToList();
        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    // Dùng policy xác định loại/số tiền refund và ghép xác nhận chuyển khoản; không xem receipt ACK là đã hoàn tiền.
    private static AdminRefundQueueItemResponse ToResponse(RefundQueueRecord record)
    {
        // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
        RentalRefundKind kind = RentalRefundPolicy.KindOf(record.RequestAction) ?? RentalRefundKind.FullRefund;
        decimal deduction = RentalRefundPolicy.DeductionOf(kind, record.FinalDeductionAmount);
        AdminRefundConfirmationResponse? confirmation = record.Confirmation is { } c
            ? AdminRefundMapper.ToConfirmation(c)
            : null;

        return new AdminRefundQueueItemResponse(
            record.RentalItemId,
            record.RentalOrderId,
            record.OrderCode,
            record.RenterId,
            record.RecipientName,
            record.RecipientPhone,
            record.EquipmentName,
            record.SupplierId,
            record.ItemStatus,
            kind,
            record.RentalAmount,
            record.DepositAmount,
            deduction,
            RentalRefundPolicy.AmountOf(kind, record.RentalAmount, record.DepositAmount, deduction),
            confirmation?.RefundAccount ?? OrderQueryMapper.ParseRefundAccount(record.RefundDestinationRef),
            confirmation is null ? AdminRefundQueueState.Pending : AdminRefundQueueState.Completed,
            record.RequestStatus,
            record.RequestedAt,
            confirmation);
    }

    // Ghi audit truy cập hàng đợi bằng metadata phân trang/trạng thái, tránh log payload tài khoản hoàn tiền.
    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} viewed refund queue (status {Status}, page {Page}, {Count} items)")]
    private static partial void LogAdminViewedRefunds(ILogger logger, Guid? adminId, string? status, int page, int count);
}
