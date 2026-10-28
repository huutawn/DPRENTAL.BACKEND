using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Refunds;

/// <summary>
/// Danh sách đơn yêu cầu hoàn tiền cho Admin chuyển khoản. Chỉ Admin (không phải service token) được xem.
/// </summary>
public interface IAdminRefundQueryService
{
    Task<Result<PagedResponse<AdminRefundQueueItemResponse>>> GetRefundsAsync(
        string? status,
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
