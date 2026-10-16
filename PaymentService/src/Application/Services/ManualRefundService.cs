using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepository;

namespace Dprental.PaymentService.Application.Services;

public sealed class ManualRefundService(IManualRefundRepository refunds) : IManualRefundService
{
    public async Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await refunds.CompleteAsync(
            requirementId, request.ExternalTransferReference, adminId, cancellationToken);
        if (result.IsFailure) return result.Error;

        var value = result.Value;
        return Result.Success(new CompleteManualRefundResponse(
            value.RequirementId, value.RequirementRef, value.RentalItemId,
            value.SettlementRef, value.Amount, value.ExternalTransferReference,
            value.CompletedAt, value.CompletedBy, value.IsReplay));
    }
}
