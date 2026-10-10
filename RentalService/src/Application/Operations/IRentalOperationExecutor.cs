using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Operations;

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
}
