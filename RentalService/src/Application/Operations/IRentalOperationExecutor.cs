using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Operations;

/// <summary>
/// DTO phản hồi sau khi Quản trị viên (Admin) replay thành công một tác vụ.
/// </summary>
/// <param name="OrderId">Mã định danh duy nhất của đơn thuê liên quan.</param>
/// <param name="OperationId">Mã định danh tác vụ được replay.</param>
/// <param name="Action">Hành động của tác vụ nghiệp vụ.</param>
/// <param name="Status">Trạng thái mới của tác vụ sau khi được đặt lại để chạy lại.</param>
/// <param name="RetryCount">Số lần đã thử lại (được đặt lại về 0).</param>
/// <param name="ReplayedAt">Thời điểm ghi nhận yêu cầu replay.</param>
public sealed record AdminReplayResponse(
    Guid OrderId,
    string OperationId,
    string Action,
    string Status,
    int RetryCount,
    DateTimeOffset ReplayedAt);

public interface IRentalOperationExecutor
{
    Task<Result> ExecuteExtendHoldAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierDecisionResponse>> ExecuteSupplierAcceptAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierDecisionResponse>> ExecuteSupplierRejectAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result> ExecuteCancelInvoiceAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<Result> ExecuteReleaseAllocationAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<Result> ExecuteRequestRefundAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<Result> ExecuteReconcileAcceptAsync(
        Guid orderId,
        Guid itemId,
        string acceptOperationKey,
        CancellationToken cancellationToken = default);

    Task<Result> ExecutePaymentExpiryAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<Result> ReplayStoredOperationAsync(
        Guid orderId,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<Result> ExecuteScheduledOperationAsync(
        OperationReceiptDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<Result<AdminReplayResponse>> ExecuteAdminReplayAsync(
        Guid orderId,
        string operationId,
        Guid adminActorId,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
