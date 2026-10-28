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
    public async Task<Result<PagedResponse<AdminRefundQueueItemResponse>>> GetRefundsAsync(
        string? status,
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result access = RentalAuthorizationPolicy.AuthorizeAdminOrderRead(caller);
        if (access.IsFailure)
        {
            return access.Error;
        }

        Result<AdminRefundQueueState?> state = OrderQueryMapper.ParseStatusFilter<AdminRefundQueueState>(status);
        if (state.IsFailure)
        {
            return state.Error;
        }

        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<RefundQueueRecord> records = await refunds.GetQueuePageAsync(
            state.Value is { } s ? s == AdminRefundQueueState.Completed : null,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        // Danh sách hiện đủ số tài khoản ngân hàng của người thuê: ghi lại ai đã xem.
        LogAdminViewedRefunds(logger, caller.UserId, status, normalizedPage, records.Items.Count);

        List<AdminRefundQueueItemResponse> items = records.Items.Select(ToResponse).ToList();
        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    private static AdminRefundQueueItemResponse ToResponse(RefundQueueRecord record)
    {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} viewed refund queue (status {Status}, page {Page}, {Count} items)")]
    private static partial void LogAdminViewedRefunds(ILogger logger, Guid? adminId, string? status, int page, int count);
}
