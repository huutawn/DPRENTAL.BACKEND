using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.SupplierDecision;

public interface ISupplierDecisionService
{
    Task<Result<SupplierDecisionResponse>> AcceptItemAsync(
        Guid itemId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierDecisionResponse>> RejectItemAsync(
        Guid itemId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<HandoverItemResponse>> HandoverItemAsync(
        Guid itemId,
        HandoverItemRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
