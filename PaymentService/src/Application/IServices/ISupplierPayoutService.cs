using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface ISupplierPayoutService
{
    Task<Result<AttachPayoutDestinationResponse>> AttachDestinationAsync(
        Guid payoutId, Guid adminId, AttachPayoutDestinationRequest request, CancellationToken ct);

    Task<Result<CompleteSupplierPayoutResponse>> CompleteAsync(
        Guid payoutId, Guid adminId, CompleteSupplierPayoutRequest request, CancellationToken ct);
}
