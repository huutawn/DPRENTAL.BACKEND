using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IManualRefundService
{
    Task<Result<RequestRefundResponse>> RequestAsync(RequestRefundRequest request, CancellationToken ct);
    Task<Result<ManualRefundDetails>> GetAsync(Guid id, CancellationToken ct);
    Task<Result<ManualRefundPage>> ListAsync(int status, Guid? cursor, int limit, CancellationToken ct);
    Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId,
        CancellationToken ct);
}
