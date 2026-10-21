using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Cancellation;

/// <summary>
/// Renter hủy cả đơn khi đang chờ thanh toán.
/// </summary>
public interface IRenterOrderCancellationService
{
    Task<Result<CancelOrderResponse>> CancelOrderAsync(
        Guid orderId,
        CancelOrderRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
