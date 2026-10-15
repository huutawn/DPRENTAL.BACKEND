using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Settlement;

public interface IDeductionService
{
    Task<Result<SubmitDeductionResponse>> SubmitAsync(
        Guid itemId, IRentalCallerContext caller, SubmitDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);

    Task<int> ProcessDeductionTimeoutsAsync(CancellationToken cancellationToken = default);

    Task<Result> ExpireSingleItemDeductionAsync(Guid itemId, CancellationToken cancellationToken = default);
}