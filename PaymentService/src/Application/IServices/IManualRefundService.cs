using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IManualRefundService
{
    Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId,
        CancellationToken cancellationToken);
}
