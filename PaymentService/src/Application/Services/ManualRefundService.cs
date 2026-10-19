using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepository;

namespace Dprental.PaymentService.Application.Services;

public sealed class ManualRefundService(IManualRefundRepository refunds) : IManualRefundService
{
    private const int MaxOperationKey = 64;
    private const int MaxTransferReference = 256;
    private const int MaxAuditReason = 500;

    public async Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (requirementId == Guid.Empty || adminId == Guid.Empty)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Thông tin xác nhận hoàn cọc không hợp lệ.");
        if (!IsClean(request.OperationKey, MaxOperationKey))
            return Error.Validation("Payment.OperationKeyInvalid", $"OperationKey bắt buộc, tối đa {MaxOperationKey} ký tự.");
        if (!IsClean(request.ExternalTransferReference, MaxTransferReference))
            return Error.Validation("Payment.TransferReferenceInvalid", $"ExternalTransferReference bắt buộc, tối đa {MaxTransferReference} ký tự.");
        if (!IsClean(request.AuditReason, MaxAuditReason))
            return Error.Validation("Payment.AuditReasonInvalid", $"AuditReason bắt buộc, tối đa {MaxAuditReason} ký tự.");

        var operationKey = request.OperationKey.Trim();

        var result = await refunds.CompleteAsync(new CompleteManualRefundCommand(
            requirementId, adminId, operationKey,
            request.ExternalTransferReference.Trim(), request.AuditReason.Trim()), cancellationToken);

        if (result.IsFailure) return result.Error;

        var c = result.Value;
        return Result.Success(new CompleteManualRefundResponse(
            c.RequirementId, c.RequirementRef, c.RentalItemId, c.SettlementRef, c.Amount,
            c.ExternalTransferReference, c.CompletedAt, c.CompletedBy, c.IsReplay, operationKey));

        static bool IsClean(string? s, int max) =>
            !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);
    }
}
