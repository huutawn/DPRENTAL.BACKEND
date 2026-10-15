using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Settlement;

public interface IDeductionResponseService
{
    Task<Result<RespondDeductionResponse>> RespondAsync(
        Guid itemId, IRentalCallerContext caller, RespondDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);

    Task<int> ProcessRenterResponseTimeoutsAsync(CancellationToken cancellationToken = default);

    Task<Result> ExpireSingleRenterResponseAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<Result<ResolveDisputeResponse>> ResolveDisputeAsync(Guid itemId, IRentalCallerContext caller,
        ResolveDisputeRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Result<DisputeDetailDto>> GetDisputeDetailAsync(Guid itemId, IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DisputeSummaryDto>>> GetOpenDisputesAsync(IRentalCallerContext caller,
        int skip, int take, CancellationToken cancellationToken = default);
}
