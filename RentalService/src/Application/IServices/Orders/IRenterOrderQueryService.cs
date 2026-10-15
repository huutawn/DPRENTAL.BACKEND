using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Người thuê xem đơn của chính mình: đơn cha cùng toàn bộ đơn con và thiết bị.
/// </summary>
public interface IRenterOrderQueryService
{
    Task<Result<PagedResponse<RenterOrderSummaryResponse>>> GetMyOrdersAsync(
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<RenterOrderDetailResponse>> GetMyOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
