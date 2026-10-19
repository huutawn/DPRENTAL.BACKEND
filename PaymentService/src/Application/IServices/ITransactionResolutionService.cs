using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface ITransactionResolutionService
{
    Task<Result<ResolveUnmatchedTransactionResponse>> ResolveAsync(
        Guid transactionId, Guid adminId, ResolveUnmatchedTransactionRequest request,
        CancellationToken cancellationToken);
}
