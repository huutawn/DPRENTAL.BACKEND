using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Cancellation;

public interface IItemCancellationService
{
    Task<Result<CancelNotDeliveredResponse>> CancelByRenterAsync(
        Guid itemId,
        CancelNotDeliveredRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> ExpireNoHandoverAsync(
        Guid itemId,
        string reason,
        string operationKey,
        CancellationToken cancellationToken = default);
}
