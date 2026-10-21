using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Services.Cancellation;

/// <summary>
/// Lớp mỏng: xác định danh tính renter qua <see cref="RentalAuthorizationPolicy"/> rồi giao cho executor.
/// Kiểm tra sở hữu đơn, khóa order, idempotency và toàn bộ quy trình hủy nằm trong
/// <see cref="IRentalOperationExecutor.ExecuteRenterCancelOrderAsync"/>.
/// </summary>
public sealed class RenterOrderCancellationService(IRentalOperationExecutor operationExecutor)
    : IRenterOrderCancellationService
{
    public async Task<Result<CancelOrderResponse>> CancelOrderAsync(
        Guid orderId,
        CancelOrderRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> renter = RentalAuthorizationPolicy.AuthorizeRenterCaller(caller);
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        return await operationExecutor.ExecuteRenterCancelOrderAsync(
            orderId,
            renter.Value,
            request?.Reason,
            idempotencyKey,
            cancellationToken);
    }
}
