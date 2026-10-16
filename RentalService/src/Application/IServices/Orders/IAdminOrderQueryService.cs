using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Admin tìm mọi đơn thuê và xem chi tiết kèm mã nội bộ, máy cụ thể và lịch sử trạng thái.
/// </summary>
public interface IAdminOrderQueryService
{
    Task<Result<PagedResponse<AdminOrderSummaryResponse>>> SearchOrdersAsync(
        AdminOrderSearchRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<AdminOrderDetailResponse>> GetOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
