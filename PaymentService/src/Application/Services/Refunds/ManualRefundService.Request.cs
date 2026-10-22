using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

public sealed partial class ManualRefundService
{
    public async Task<Result<RequestRefundResponse>> RequestAsync(RequestRefundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var valid = request.Validate();
        if (valid.IsFailure) return valid.Error;
        request = request with
        {
            OperationKey = request.OperationKey.Trim(),
            InvoiceRef = request.InvoiceRef.Trim(),
            PaymentRef = request.PaymentRef.Trim(),
            Reason = request.Reason.Trim(),
            Items = request.Items.OrderBy(x => x.RentalItemId).ToArray()
        };
        string fingerprint = CanonicalJournalHasher.ComputeOutboxDigest(JsonSerializer.Serialize(request));
        var stored = await invoices.GetOperationReceiptAsync(request.OperationKey, ct);
        if (stored is not null) return ReadReplay<RequestRefundResponse>(stored, "RequestRefund", fingerprint, x => x with { IsReplay = true });
        var verified = await integrityChecker.VerifyAsync(new TransactionVerificationRequest(request.PaymentRef,
            VerificationPurposes.RefundRequest, Guid.NewGuid().ToString("N"), InvoiceRef: request.InvoiceRef,
            ExpectedOrderId: request.RentalOrderId, ExpectedCurrency: "VND", CallerIdentity: "rental-service"), ct);
        if (verified.IsFailure) return verified.Error;
        var evidence = VerifiedWriteContext.FromResponse(verified.Value, VerificationPurposes.RefundRequest, clock.GetUtcNow());
        if (evidence.IsFailure) return evidence.Error;
        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        var invoice = await invoices.FindInvoiceWithRowLockAsync(request.InvoiceRef, ct);
        if (invoice is null) return Error.NotFound("INVOICE_NOT_FOUND", "Invoice not found.");
        await unitOfWork.ExecuteStreamLockAsync("operation:" + request.OperationKey, ct);
        stored = await invoices.GetOperationReceiptAsync(request.OperationKey, ct);
        if (stored is not null) return ReadReplay<RequestRefundResponse>(stored, "RequestRefund", fingerprint, x => x with { IsReplay = true });
        if (invoice.RentalOrderId != request.RentalOrderId || invoice.PaymentRef != request.PaymentRef ||
            invoice.Status is not (InvoiceStatus.Paid or InvoiceStatus.Expired or InvoiceStatus.Cancelled))
            return Error.Conflict("Payment.RefundInvoiceMismatch", "Refund does not match the received payment.");
        var required = new List<VerifiedWriteClaim> { SettlementService.ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceClaim(invoice)) };
        var existing = await refunds.GetForInvoiceAsync(invoice.Id, ct);
        var lines = new List<ManualRefundItem>();
        foreach (var requested in request.Items)
        {
            var item = invoice.Items.SingleOrDefault(x => x.RentalItemId == requested.RentalItemId);
            if (item is null) return Error.NotFound("Payment.InvoiceItemNotFound", "Refund item does not belong to the invoice.");
            required.Add(SettlementService.ToWriteClaim(OperationalSnapshotEngine.CreateInvoiceItemClaim(item)));
            if (await ledger.FindByItemAsync(item.RentalItemId, ct) is not null)
                return Error.Conflict("Payment.ItemAlreadySettled", "Item already has a settlement.");
            var reserved = existing.SelectMany(x => x.Items).Where(x => x.RentalItemId == item.RentalItemId).ToArray();
            if (reserved.Sum(x => x.RentalAmount) + requested.RefundRentalAmount > item.RentalAmount ||
                reserved.Sum(x => x.DepositAmount) + requested.RefundDepositAmount > item.DepositAmount)
                return Error.Conflict("REFUND_EXCEEDS_PAID_AMOUNT", "Refund exceeds the unreserved paid amount.");
            lines.Add(ManualRefundItem.Create(item.RentalItemId, requested.RefundRentalAmount, requested.RefundDepositAmount));
        }
        if (existing.Sum(x => x.Amount) + request.TotalRefundAmount > invoice.TotalAmount)
            return Error.Conflict("REFUND_EXCEEDS_PAID_AMOUNT", "Refund exceeds the received invoice payment.");
        var guard = evidence.Value.Validate(clock.GetUtcNow(), required.ToArray());
        if (guard.IsFailure) return guard.Error;
        var now = clock.GetUtcNow();
        var refund = ManualRefundRequirement.Create(invoice.Id, invoice.RenterId, request.PaymentRef, request.OperationKey,
            request.Reason, ManualRefundKind.RentalRefund, null, lines, now);
        await refunds.AddAsync(refund, ct);
        var response = new RequestRefundResponse(refund.RequirementRef, request.OperationKey, "Accepted", refund.Amount, invoice.Currency, false);
        var receipt = OperationReceipt.Create(request.OperationKey, "RequestRefund", fingerprint,
            JsonSerializer.Serialize(response), invoice.InvoiceRef, invoice.RentalOrderId, now);
        var journal = await JournalEntryFactory.CreateNextAsync(invoices, Guid.NewGuid(), refund.RequirementRef,
            JournalStreamTypes.ManualRefund, JournalEventTypes.RefundRequired, invoice, refund.Amount,
            "PENDING_MANUAL_REFUND", "rental-service", now,
            [OperationalSnapshotEngine.CreateManualRefundClaim(refund), OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt)],
            encryptor, null, ct);
        if (clock.GetUtcNow() >= evidence.Value.ExpiresAt) return Error.Conflict("Payment.ReceiptExpired", "Receipt expired before commit.");
        await invoices.SaveFinancialOperationAsync(receipt, [journal], [], ct);
        await tx.CommitAsync(ct);
        return response;
    }
}
