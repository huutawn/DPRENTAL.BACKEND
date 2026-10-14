using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.SupplierDecision;

/// <summary>
/// Supplier nhận hoặc từ chối cả đơn con của mình; không quyết định riêng từng mục thuê.
/// </summary>
public interface ISupplierDecisionService
{
    Task<Result<SubOrderDecisionResponse>> AcceptSubOrderAsync(
        Guid subOrderId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);

    Task<Result<SubOrderDecisionResponse>> RejectSubOrderAsync(
        Guid subOrderId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
