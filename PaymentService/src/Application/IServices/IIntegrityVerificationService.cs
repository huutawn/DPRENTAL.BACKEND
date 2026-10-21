using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IIntegrityVerificationService
{
    Task<Result<TransactionVerificationResponse>> VerifyAsync(
        TransactionVerificationRequest request,
        CancellationToken cancellationToken = default);
}
