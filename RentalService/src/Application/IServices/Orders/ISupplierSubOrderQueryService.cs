using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Nhà cung cấp xem đơn con của mình, chỉ sau khi người thuê đã thanh toán đúng hạn.
/// </summary>
public interface ISupplierSubOrderQueryService
{
    Task<Result<PagedResponse<SupplierSubOrderSummaryResponse>>> GetMySubOrdersAsync(
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierSubOrderDetailResponse>> GetMySubOrderAsync(
        Guid subOrderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
