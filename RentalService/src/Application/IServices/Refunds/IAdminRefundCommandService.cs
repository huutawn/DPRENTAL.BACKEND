using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Refunds;

/// <summary>
/// Admin xác nhận đã chuyển khoản hoàn tiền cho người thuê. Chỉ Admin (không phải service token) được xác nhận.
/// </summary>
public interface IAdminRefundCommandService
{
    Task<Result<AdminOrderRefundResponse>> ConfirmTransferAsync(
        Guid rentalItemId,
        ConfirmRefundTransferRequest? request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
