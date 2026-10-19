using Dprental.PaymentService.Common;
using Dprental.PaymentService.Application.DTOs.Payout;

namespace Dprental.PaymentService.Application.IServices;

public interface ISupplierPayoutDestinationService
{
    Task<Result<PayoutDestinationResponse>> RegisterAsync(Guid supplierId, Guid adminId,
        RegisterPayoutDestinationRequest request, CancellationToken ct);
    Task<Result<IReadOnlyList<PayoutDestinationResponse>>> ListAsync(Guid supplierId, CancellationToken ct);
}
