using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface ISupplierPayoutService
{
    Task<Result<CompleteSupplierPayoutResponse>> CompleteAsync(
        Guid payoutId, Guid adminId, CompleteSupplierPayoutRequest request, CancellationToken ct);

    Task<Result<CompleteSupplierPayoutResponse>> CompleteFromBankAsync(
        Guid payoutId, string paymentRef, CancellationToken ct);
}
