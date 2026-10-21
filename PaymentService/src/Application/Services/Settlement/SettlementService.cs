using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Application.Services;

public sealed class SettlementService(
    IInvoiceRepository invoices, ISettlementLedger ledger, ITransactionIntegrityChecker integrityChecker,
    IManualRefundRepository refunds, IUnitOfWork unitOfWork, IPaymentDataEncryptor encryptor, TimeProvider clock) : ISettlementService
{
    public async Task<Result<SettleRentalResponse>> SettleAsync(SettleRentalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = request.Validate();
        if (validation.IsFailure) return validation.Error;
        string fingerprint = CanonicalJournalHasher.ComputeOutboxDigest(JsonSerializer.Serialize(request with
        {
            InvoiceRef = request.InvoiceRef.Trim(), PaymentRef = request.PaymentRef.Trim(),
            DecisionRef = request.DecisionRef.Trim(), OperationKey = request.OperationKey.Trim(), Currency = "VND"
        }));
        var replay = await invoices.GetOperationReceiptAsync(request.OperationKey.Trim(), ct);
        if (replay is not null) return Replay(replay, fingerprint);
        var verified = await integrityChecker.VerifyAsync(new TransactionVerificationRequest(
            request.PaymentRef.Trim(), VerificationPurposes.Settlement, Guid.NewGuid().ToString("N"),
            ExpectedCurrency: "VND", ExpectedStatus: InvoiceStatus.Paid, InvoiceRef: request.InvoiceRef.Trim(),
            ExpectedOrderId: request.RentalOrderId, RentalItemId: request.RentalItemId,
            SupplierId: request.SupplierId, CallerIdentity: "rental-service"), ct);
        if (verified.IsFailure) return verified.Error;
        var evidence = VerifiedWriteContext.FromResponse(verified.Value, VerificationPurposes.Settlement, clock.GetUtcNow());
        if (evidence.IsFailure) return evidence.Error;

        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        var invoice = await invoices.FindInvoiceWithRowLockAsync(request.InvoiceRef.Trim(), ct);
        if (invoice is null) return Error.NotFound("INVOICE_NOT_FOUND", "Invoice not found.");
        await unitOfWork.ExecuteStreamLockAsync("operation:" + request.OperationKey.Trim(), ct);
        replay = await invoices.GetOperationReceiptAsync(request.OperationKey.Trim(), ct);
        if (replay is not null) return Replay(replay, fingerprint);
        var item = invoice.Items.SingleOrDefault(x => x.RentalItemId == request.RentalItemId);
        if (item is null) return Error.NotFound("Payment.InvoiceItemNotFound", "Invoice item not found.");
        var guarded = evidence.Value.Validate(clock.GetUtcNow(), ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceClaim(invoice)),
            ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceItemClaim(item)));
        if (guarded.IsFailure) return guarded.Error;
        if (invoice.Status != InvoiceStatus.Paid || invoice.PaymentRef != request.PaymentRef.Trim() ||
            invoice.RentalOrderId != request.RentalOrderId || item.SupplierId != request.SupplierId || item.DepositAmount != request.DepositAmount)
            return Error.Conflict("Payment.SettlementSnapshotMismatch", "Settlement does not match the paid invoice snapshot.");
        var existing = await ledger.FindByItemAsync(item.RentalItemId, ct);
        if (existing is not null) return Error.Conflict("DUPLICATE_SETTLEMENT", "Item already has a settlement.");
        var reservedRefunds = await refunds.GetForInvoiceAsync(invoice.Id, ct);
        if (reservedRefunds.Any(x => x.Kind == ManualRefundKind.RentalRefund && x.Items.Any(i => i.RentalItemId == item.RentalItemId)))
            return Error.Conflict("Payment.ItemAlreadyRefunded", "Item is already reserved for cancellation refund.");
        var calculated = Calculate(item, request.FinalDeductionAmount, request.DecisionRef, request.OperationKey);
        if (calculated.IsFailure) return calculated.Error;
        var amounts = calculated.Value;
        var now = clock.GetUtcNow();
        var record = RentalItemSettlementRecord.From(amounts, invoice,
            amounts.DepositRefundDue > 0 ? SettlementState.PendingManualRefund : SettlementState.Completed, now);
        record.Entries.Add(SettlementLedgerEntry.Create(record.Id, SettlementEntryType.SupplierPayable, amounts.SupplierPayable, now));
        record.Entries.Add(SettlementLedgerEntry.Create(record.Id, SettlementEntryType.PlatformFee, amounts.PlatformFeeAmount, now));
        record.Entries.Add(SettlementLedgerEntry.Create(record.Id, SettlementEntryType.DepositRefundDue, amounts.DepositRefundDue, now));
        await ledger.AddAsync(record, ct);
        var response = new SettleRentalResponse(record.SettlementRef, amounts.OperationKey,
            amounts.DepositRefundDue > 0 ? SettlementStatus.PendingManualRefund : SettlementStatus.Completed,
            amounts.FinalDeductionAmount, amounts.DepositRefundDue, invoice.Currency, false);
        var receipt = OperationReceipt.Create(amounts.OperationKey, "SettleRentalItem", fingerprint,
            JsonSerializer.Serialize(response), invoice.InvoiceRef, invoice.RentalOrderId, now);
        var claims = new List<CanonicalEntityClaim>
        {
            OperationalSnapshotEngine.CreateSettlementClaim(record), OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt)
        };
        var journals = new List<TransactionJournalEntry>();
        var messages = new List<OutboxMessage>();
        Guid operationId = Guid.NewGuid();
        if (amounts.DepositRefundDue > 0)
        {
            var refund = ManualRefundRequirement.Create(invoice.Id, invoice.RenterId, invoice.PaymentRef!, amounts.OperationKey,
                "RENTAL_DEPOSIT_REFUND", ManualRefundKind.SettlementDeposit, record.Id,
                [ManualRefundItem.Create(item.RentalItemId, 0, amounts.DepositRefundDue)], now);
            record.AssignRefundRequirement(refund);
            claims.Add(OperationalSnapshotEngine.CreateManualRefundClaim(refund));
            journals.Add(await JournalEntryFactory.CreateNextAsync(invoices, operationId, refund.RequirementRef,
                JournalStreamTypes.ManualRefund, JournalEventTypes.RefundRequired, invoice, refund.Amount,
                "PENDING_MANUAL_REFUND", "rental-service", now, claims, encryptor, null, ct));
        }
        else messages.Add(PaymentOutboxFactory.CreateRentalSettled(record, invoice, null, now));
        journals.Add(await JournalEntryFactory.CreateNextAsync(invoices, operationId, record.SettlementRef,
            JournalStreamTypes.RentalSettlement, JournalEventTypes.RentalSettled, invoice,
            invoice.Items.Single(x => x.RentalItemId == record.RentalItemId).TotalAmount, response.Status,
            "rental-service", now, claims, encryptor, messages.FirstOrDefault(), ct));
        guarded = evidence.Value.Validate(clock.GetUtcNow(), ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceClaim(invoice)),
            ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceItemClaim(item)));
        if (guarded.IsFailure) return guarded.Error;
        await invoices.SaveFinancialOperationAsync(receipt, journals, messages, ct);
        await tx.CommitAsync(ct);
        return response;
    }

    internal static VerifiedWriteClaim ToWriteClaim(CanonicalEntityClaim claim) => new(claim.EntityType, claim.EntityId, claim.Revision, claim.Digest);

    private static Result<SettleRentalResponse> Replay(OperationReceipt receipt, string fingerprint)
    {
        if (receipt.OperationType != "SettleRentalItem" || receipt.PayloadFingerprint != fingerprint)
            return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey was used with different content.");
        try
        {
            var response = JsonSerializer.Deserialize<SettleRentalResponse>(receipt.ResultJson);
            return response is null ? Error.Failure("Payment.CorruptedReceipt", "Stored response is missing.") : response with { IsReplay = true };
        }
        catch (JsonException) { return Error.Failure("Payment.CorruptedReceipt", "Stored response is invalid."); }
    }

    public static Result<RentalItemSettlement> Calculate(InvoiceItem item, decimal deduction, string decisionRef, string operationKey)
    {
        if (string.IsNullOrWhiteSpace(decisionRef))
            return Error.Validation("Payment.InvalidDecisionRef", "DecisionRef must contain 1 to 256 characters.");
        if (decisionRef.Trim().Length > 256) return Error.Validation("Payment.DecisionRefTooLong", "DecisionRef is longer than 256 characters.");
        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 64)
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey must contain 1 to 64 characters.");
        var valid = VndAmountValidator.ValidateVndAmount(deduction, "Deduction");
        if (valid.IsFailure) return valid.Error;
        if (deduction > item.DepositAmount) return Error.Validation("Payment.DeductionExceedsDeposit", "Deduction exceeds deposit.");
        decimal fee = Math.Round(item.RentalAmount * 0.10m, 0, MidpointRounding.AwayFromZero);
        return new RentalItemSettlement(operationKey.Trim(), item.InvoiceId, item.Id, item.RentalItemId,
            item.SupplierId, decisionRef.Trim(), item.RentalAmount, item.DepositAmount, deduction,
            item.DepositAmount - deduction, 0.10m, fee, item.RentalAmount - fee + deduction);
    }
}
