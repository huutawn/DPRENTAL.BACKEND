using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepositories;
namespace Dprental.PaymentService.Application.Services;

public sealed class ManualRefundService(
    IManualRefundRepository refunds,
    ITransactionIntegrityChecker integrityChecker) : IManualRefundService
{
    public async Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read-only replay check without side-effects before verification
        var existingReplay = await refunds.GetCompletedReplayAsync(requirementId, request.ExternalTransferReference, cancellationToken);
        if (existingReplay is not null)
        {
            return Result.Success(new CompleteManualRefundResponse(
                existingReplay.RequirementId, existingReplay.RequirementRef, existingReplay.RentalItemId,
                existingReplay.SettlementRef, existingReplay.Amount, existingReplay.ExternalTransferReference,
                existingReplay.CompletedAt, existingReplay.CompletedBy, IsReplay: true));
        }

        string reqRef = $"MRF-{requirementId:N}";
        var verifyRes = await integrityChecker.VerifyAsync(new Dprental.PaymentService.Application.DTOs.Verification.TransactionVerificationRequest(
            PaymentRef: reqRef,
            Purpose: Dprental.PaymentService.Application.DTOs.Verification.VerificationPurposes.ManualRefundConfirmation,
            CallerNonce: Guid.NewGuid().ToString("N"),
            CallerIdentity: $"ADMIN:{adminId:D}",
            RequirementRef: reqRef), cancellationToken);

        if (verifyRes.IsFailure)
        {
            return verifyRes.Error;
        }

        if (!verifyRes.Value.Usability.IsUsable)
        {
            return Error.Conflict("Payment.RefundIntegrityFailed", $"Yêu cầu hoàn cọc chưa được xác minh toàn vẹn: {verifyRes.Value.Usability.Reason}");
        }

        var claims = (verifyRes.Value.Receipt!.Body.EntityClaims ?? [])
            .Select(c => new VerifiedWriteClaim(c.EntityType, c.EntityId, c.Revision, c.Digest))
            .ToList();

        var verifiedContext = new VerifiedWriteContext(
            verifyRes.Value.Receipt.ReceiptId,
            verifyRes.Value.Receipt.KeyId,
            verifyRes.Value.Receipt.ExpiresAt,
            claims,
            DateTimeOffset.UtcNow);
        var result = await refunds.CompleteAsync(requirementId, request.ExternalTransferReference, adminId, cancellationToken);
        if (result.IsFailure) return result.Error;
        var value = result.Value;
        return Result.Success(new CompleteManualRefundResponse(
            value.RequirementId, value.RequirementRef, value.RentalItemId,
            value.SettlementRef, value.Amount, value.ExternalTransferReference,
            value.CompletedAt, value.CompletedBy, value.IsReplay));
    }
}
