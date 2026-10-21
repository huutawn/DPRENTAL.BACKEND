using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
namespace Dprental.PaymentService.Application.Services;

public sealed partial class ManualRefundService(
    IManualRefundRepository refunds, ITransactionIntegrityChecker integrityChecker,
    IInvoiceRepository invoices, ISettlementLedger ledger, IUnitOfWork unitOfWork,
    IPaymentDataEncryptor encryptor, TimeProvider clock) : IManualRefundService
{
    public async Task<Result<CompleteManualRefundResponse>> CompleteAsync(
        Guid requirementId, CompleteManualRefundRequest request, Guid adminId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (requirementId == Guid.Empty || adminId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExternalTransferReference) ||
            request.ExternalTransferReference.Trim().Length > 256 || string.IsNullOrWhiteSpace(request.OperationKey) || request.OperationKey.Trim().Length > 64)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Valid requirement, transfer reference and OperationKey are required.");
        string fingerprint = CanonicalJournalHasher.ComputeOutboxDigest(JsonSerializer.Serialize(new
        {
            RequirementId = requirementId, Reference = request.ExternalTransferReference.Trim(), AdminId = adminId
        }));
        var stored = await invoices.GetOperationReceiptAsync(request.OperationKey.Trim(), ct);
        if (stored is not null) return ReadReplay<CompleteManualRefundResponse>(stored, "ConfirmManualRefund", fingerprint,
            x => x with { IsReplay = true });
        var snapshot = await refunds.FindAsync(requirementId, ct);
        if (snapshot is null) return Error.NotFound("Payment.ManualRefundNotFound", "Refund requirement not found.");
        var verified = await integrityChecker.VerifyAsync(new TransactionVerificationRequest(snapshot.RequirementRef,
            VerificationPurposes.ManualRefundConfirmation, Guid.NewGuid().ToString("N"), ExpectedAmount: snapshot.Amount,
            RequirementRef: snapshot.RequirementRef, CallerIdentity: $"ADMIN:{adminId:D}"), ct);
        if (verified.IsFailure) return verified.Error;
        var evidence = VerifiedWriteContext.FromResponse(verified.Value, VerificationPurposes.ManualRefundConfirmation, clock.GetUtcNow());
        if (evidence.IsFailure) return evidence.Error;
        var invSnapshot = await invoices.GetInvoiceSnapshotByIdAsync(snapshot.InvoiceId, ct);
        if (invSnapshot is null) return Error.NotFound("INVOICE_NOT_FOUND", "Invoice not found.");
        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        var invoice = await invoices.FindInvoiceWithRowLockAsync(invSnapshot.InvoiceRef, ct);
        if (invoice is null) return Error.NotFound("INVOICE_NOT_FOUND", "Invoice not found.");
        await unitOfWork.ExecuteStreamLockAsync("operation:" + request.OperationKey.Trim(), ct);
        stored = await invoices.GetOperationReceiptAsync(request.OperationKey.Trim(), ct);
        if (stored is not null) return ReadReplay<CompleteManualRefundResponse>(stored, "ConfirmManualRefund", fingerprint, x => x with { IsReplay = true });
        var refund = await refunds.FindWithRowLockAsync(requirementId, ct);
        if (refund is null) return Error.NotFound("Payment.ManualRefundNotFound", "Refund requirement not found.");
        var guard = evidence.Value.Validate(clock.GetUtcNow(), SettlementService.ToWriteClaim(OperationalSnapshotEngine.CreateManualRefundClaim(refund)));
        if (guard.IsFailure) return guard.Error;
        if (refund.Status != ManualRefundStatus.PendingManualRefund)
            return Error.Conflict("Payment.RefundAlreadyCompleted", "Refund was already completed by another operation.");
        string transferRef = request.ExternalTransferReference.Trim();
        await unitOfWork.ExecuteStreamLockAsync("transfer:" + transferRef, ct);
        if (await refunds.TransferReferenceExistsAsync(transferRef, requirementId, ct))
            return Error.Conflict("Payment.TransferReferenceAlreadyUsed", "Transfer reference was already used.");
        RentalItemSettlementRecord? settlement = refund.SettlementId.HasValue
            ? await ledger.FindByIdWithRowLockAsync(refund.SettlementId.Value, ct) : null;
        if (refund.Kind == ManualRefundKind.SettlementDeposit &&
            (settlement is null || settlement.Status != SettlementState.PendingManualRefund || settlement.DepositRefundDue != refund.Amount))
            return Error.Conflict("Payment.RefundSettlementMismatch", "Refund does not match the pending settlement.");
        var now = clock.GetUtcNow();
        refund.AssignCompleted(transferRef, adminId, now);
        settlement?.AssignCompleted(now);
        var response = new CompleteManualRefundResponse(refund.Id, refund.RequirementRef, refund.RentalItemId,
            settlement?.SettlementRef, refund.Amount, transferRef, now, adminId, false, request.OperationKey.Trim());
        var receipt = OperationReceipt.Create(request.OperationKey.Trim(), "ConfirmManualRefund", fingerprint,
            JsonSerializer.Serialize(response), invoice.InvoiceRef, invoice.RentalOrderId, now);
        var claims = new List<CanonicalEntityClaim> { OperationalSnapshotEngine.CreateManualRefundClaim(refund),
            OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt) };
        if (settlement is not null) claims.Add(OperationalSnapshotEngine.CreateSettlementClaim(settlement));
        var message = settlement is null ? PaymentOutboxFactory.CreateRefundCompleted(refund, invoice, now)
            : PaymentOutboxFactory.CreateRentalSettled(settlement, invoice, transferRef, now);
        Guid operationId = Guid.NewGuid();
        var journals = new List<TransactionJournalEntry>
        {
            await JournalEntryFactory.CreateNextAsync(invoices, operationId, refund.RequirementRef, JournalStreamTypes.ManualRefund,
                JournalEventTypes.RefundConfirmed, invoice, refund.Amount, "COMPLETED", $"ADMIN:{adminId:D}", now, claims, encryptor, message, ct)
        };
        if (settlement is not null)
            journals.Add(await JournalEntryFactory.CreateNextAsync(invoices, operationId, settlement.SettlementRef,
                JournalStreamTypes.RentalSettlement, JournalEventTypes.RentalSettled, invoice,
                settlement.RentalAmount + settlement.DepositAmount, "COMPLETED", $"ADMIN:{adminId:D}", now, claims, encryptor, null, ct));
        if (clock.GetUtcNow() >= evidence.Value.ExpiresAt) return Error.Conflict("Payment.ReceiptExpired", "Receipt expired before commit.");
        await invoices.SaveFinancialOperationAsync(receipt, journals, [message], ct);
        await tx.CommitAsync(ct);
        return response;
    }

    private static Result<T> ReadReplay<T>(OperationReceipt receipt, string operation, string fingerprint, Func<T, T> markReplay)
    {
        if (receipt.OperationType != operation || receipt.PayloadFingerprint != fingerprint)
            return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey was used with different content.");
        try
        {
            var result = JsonSerializer.Deserialize<T>(receipt.ResultJson);
            return result is null ? Error.Failure("Payment.CorruptedReceipt", "Stored response is missing.") : markReplay(result);
        }
        catch (JsonException) { return Error.Failure("Payment.CorruptedReceipt", "Stored response is invalid."); }
    }

    public async Task<Result<ManualRefundDetails>> GetAsync(Guid id, CancellationToken ct)
    {
        var refund = await refunds.FindAsync(id, ct);
        return refund is null ? Error.NotFound("Payment.ManualRefundNotFound", "Refund not found.") : ToDetails(refund);
    }

    public async Task<Result<ManualRefundPage>> ListAsync(int status, Guid? cursor, int limit, CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(ManualRefundStatus), status) || limit < 1 || limit > 100)
            return Error.Validation("Payment.InvalidRefundQuery", "Status must be 1 or 2 and limit between 1 and 100.");
        var page = await refunds.ListAsync((ManualRefundStatus)status, cursor, limit + 1, ct);
        var selected = page.Take(limit).Select(ToDetails).ToArray();
        return new ManualRefundPage(selected, page.Count > limit ? selected[^1].Id : null);
    }

    private static ManualRefundDetails ToDetails(ManualRefundRequirement x) => new(x.Id, x.RequirementRef, x.OperationKey,
        x.Kind.ToString(), x.Status.ToString(), x.InvoiceId, x.PaymentRef, x.Amount, x.Reason,
        x.Items.Select(i => new ManualRefundItemDto(i.RentalItemId, i.RentalAmount, i.DepositAmount)).ToArray(),
        x.CreatedAt, x.CompletedAt, x.ExternalTransferReference);
}
