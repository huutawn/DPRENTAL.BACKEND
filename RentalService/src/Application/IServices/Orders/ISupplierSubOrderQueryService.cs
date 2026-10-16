using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Nhà cung cấp xem đơn con của mình, chỉ sau khi người thuê đã thanh toán đúng hạn.
/// </summary>
public interface ISupplierSubOrderQueryService
{
    /// <summary>
    /// Một trang đơn con của người gọi; <paramref name="status"/> có giá trị thì chỉ lấy đơn con có dòng ở trạng thái đó.
    /// </summary>
    Task<Result<PagedResponse<SupplierSubOrderSummaryResponse>>> GetMySubOrdersAsync(
        int page,
        int pageSize,
        string? status,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierSubOrderDetailResponse>> GetMySubOrderAsync(
        Guid subOrderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
