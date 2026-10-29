using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

public sealed partial class ManualRefundService
{
    public async Task<Result<RentalRefundSyncResponse>> SynchronizeRentalAsync(
        RentalRefundSyncRequest request, CancellationToken ct)
    {
        if (request.RentalOrderId == Guid.Empty || request.RentalItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.SourceOperationKey) || request.Amount <= 0)
            return Error.Validation("Payment.InvalidRefundSync", "A valid order, item, source operation and amount are required.");
        var refund = await refunds.FindByOperationKeyAsync(request.SourceOperationKey, ct);
        if (refund is null) return Error.NotFound("Payment.ManualRefundNotFound", "Refund requirement has not been registered.");
        var invoice = await invoices.GetInvoiceSnapshotByIdAsync(refund.InvoiceId, ct);
        // The existing Rental UI confirms one item. Never confirm an entire multi-item payment requirement implicitly.
        if (invoice?.RentalOrderId != request.RentalOrderId || refund.Items.Count != 1 ||
            refund.Items[0].RentalItemId != request.RentalItemId || refund.Amount != request.Amount)
            return Error.Conflict("Payment.RefundSyncMismatch", "Refund order, item or amount does not match Rental evidence.");

        if (refund.Status == ManualRefundStatus.Completed)
        {
            if (request.BankTransactionRef is not null && refund.ExternalTransferReference != request.BankTransactionRef)
                return Error.Conflict("Payment.RefundEvidenceConflict", "Payment already recorded a different bank transfer.");
            return new RentalRefundSyncResponse(true, refund.Amount, refund.ExternalTransferReference,
                refund.CompletedBy, refund.CompletedAt);
        }
        if (request.BankTransactionRef is null)
            return new RentalRefundSyncResponse(false, refund.Amount, null, null, null);
        if (request.ConfirmedBy is null || request.ConfirmedBy == Guid.Empty || request.ConfirmedAt is null ||
            request.ConfirmedAt == default(DateTimeOffset) || request.ConfirmedAt > clock.GetUtcNow() ||
            string.IsNullOrWhiteSpace(request.BankTransactionRef) || request.BankTransactionRef.Length > 64)
            return Error.Validation("Payment.InvalidRefundEvidence", "Original admin, timestamp and bank transfer evidence are required.");

        // Reuse the authoritative financial workflow, including integrity checks, locks, journal and outbox.
        // Its timestamp is the Payment processing time; Rental retains the original confirmation timestamp.
        string auditReason = $"Rental confirmation {request.ConfirmedAt:O}. {request.Note}";
        if (auditReason.Length > 500) auditReason = auditReason[..500];
        var completed = await CompleteAsync(refund.Id, new CompleteManualRefundRequest(
            request.BankTransactionRef, $"rental-refund:{refund.Id:N}",
            auditReason), request.ConfirmedBy.Value, ct);
        if (completed.IsFailure) return completed.Error;
        return new RentalRefundSyncResponse(true, completed.Value.Amount, completed.Value.ExternalTransferReference,
            completed.Value.CompletedBy, completed.Value.CompletedAt, !completed.Value.IsReplay);
    }
}
