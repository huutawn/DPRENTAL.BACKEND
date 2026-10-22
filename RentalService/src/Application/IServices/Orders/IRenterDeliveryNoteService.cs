using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Orders;

/// <summary>
/// Người thuê sửa ghi chú giao hàng của đơn đã đặt, trước khi máy được bàn giao.
/// </summary>
public interface IRenterDeliveryNoteService
{
    Task<Result<OrderDeliveryResponse>> UpdateDeliveryNoteAsync(
        Guid orderId,
        UpdateDeliveryNoteRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
