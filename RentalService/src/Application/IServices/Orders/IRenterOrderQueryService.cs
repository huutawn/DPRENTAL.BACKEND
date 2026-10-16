using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Người thuê xem đơn của chính mình: đơn cha cùng toàn bộ đơn con và thiết bị.
/// </summary>
public interface IRenterOrderQueryService
{
    /// <summary>Một trang đơn của người gọi; <paramref name="status"/> rỗng là mọi trạng thái.</summary>
    Task<Result<PagedResponse<RenterOrderSummaryResponse>>> GetMyOrdersAsync(
        int page,
        int pageSize,
        string? status,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<RenterOrderDetailResponse>> GetMyOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
