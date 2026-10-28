using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Application.Services;

public sealed record PaidInvoiceBackfillReport(int Candidates, int Enqueued, int Skipped);

/// <summary>Repairs missing financial events without changing invoice state or replaying payment workflows.</summary>
public sealed class PaidInvoiceAnalyticsBackfillService(
    IInvoiceRepository invoices,
    ITransactionIntegrityChecker integrity,
    IPaymentDataEncryptor encryptor,
    TimeProvider clock)
{
    public async Task<Result<PaidInvoiceBackfillReport>> RunAsync(
        DateTimeOffset before, int limit, bool apply, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || before > clock.GetUtcNow())
            return Error.Validation("Payment.Backfill.InvalidInput", "Use a cutoff in the past and a limit between 1 and 100.");
        var refs = await invoices.FindPaidInvoicesMissingAnalyticsAsync(before, limit, ct);
        if (!apply) return new PaidInvoiceBackfillReport(refs.Count, 0, 0);
        int enqueued = 0;
        foreach (string invoiceRef in refs)
        {
            var result = await EnqueueAsync(invoiceRef, before, ct);
            if (result.IsFailure) return result.Error;
            if (result.Value) enqueued++;
        }
        return new PaidInvoiceBackfillReport(refs.Count, enqueued, refs.Count - enqueued);
    }

    private async Task<Result<bool>> EnqueueAsync(string invoiceRef, DateTimeOffset before, CancellationToken ct)
    {
        var snapshot = await invoices.GetInvoiceSnapshotAsync(invoiceRef, ct);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.PaymentRef))
            return Error.Conflict("Payment.Backfill.MissingPayment", "A paid invoice with its original payment reference is required.");
        if (snapshot.Status != InvoiceStatus.Paid || snapshot.PaidAt is null || snapshot.PaidAt >= before)
            return Error.Conflict("Payment.Backfill.Ineligible", "The invoice is no longer eligible.");
        var message = PaymentOutboxFactory.CreateInvoicePaidFinancialOutboxMessage(snapshot, clock.GetUtcNow());
        if (await invoices.HasOutboxMessageAsync(message.MessageId, ct)) return false;

        // Verify through IntegrityHost before opening a database transaction.
        var verified = await integrity.VerifyAsync(new TransactionVerificationRequest(
            PaymentRef: snapshot.PaymentRef, InvoiceRef: invoiceRef,
            Purpose: VerificationPurposes.PaymentConfirmation, CallerNonce: Guid.NewGuid().ToString("N"),
            ExpectedAmount: snapshot.TotalAmount,
            ExpectedCurrency: snapshot.Currency, ExpectedOrderId: snapshot.RentalOrderId,
            CallerIdentity: "payment-api:analytics-backfill"), ct);
        if (verified.IsFailure) return verified.Error;
        var evidence = VerifiedWriteContext.FromResponse(verified.Value,
            VerificationPurposes.PaymentConfirmation, clock.GetUtcNow());
        if (evidence.IsFailure) return evidence.Error;

        await using var tx = await invoices.BeginTransactionAsync(ct);
        await invoices.ExecuteStreamLockAsync(invoiceRef, ct);
        var invoice = await invoices.FindInvoiceWithRowLockAsync(invoiceRef, ct);
        // A concurrent backfill or payment publisher may have created the same stable message.
        if (await invoices.HasOutboxMessageAsync(message.MessageId, ct)) return false;
        if (invoice is null || invoice.Status != InvoiceStatus.Paid || invoice.PaidAt is null || invoice.PaidAt >= before)
            return Error.Conflict("Payment.Backfill.Ineligible", "The invoice is no longer eligible.");
        var preceding = await invoices.GetLatestJournalEntryAsync(invoiceRef, ct);
        if (preceding is null || !preceding.IsConfirmed)
            return Error.Conflict("Payment.Backfill.UnconfirmedJournal", "A confirmed invoice journal is required.");
        var invoiceClaim = OperationalSnapshotEngine.CreateInvoiceClaim(invoice);
        var guard = evidence.Value.Validate(clock.GetUtcNow(), new VerifiedWriteClaim(
            invoiceClaim.EntityType, invoiceClaim.EntityId, invoiceClaim.Revision, invoiceClaim.Digest));
        if (guard.IsFailure) return guard.Error;
        message = PaymentOutboxFactory.CreateInvoicePaidFinancialOutboxMessage(invoice, clock.GetUtcNow());
        var claims = new[] { invoiceClaim, OperationalSnapshotEngine.CreateOutboxClaim(message) };
        var journal = await JournalEntryFactory.CreateNextAsync(invoices, Guid.NewGuid(), invoiceRef,
            JournalStreamTypes.Invoice, JournalEventTypes.InvoicePaidFinancialLinked, invoice,
            invoice.TotalAmount, invoice.Status, "SYSTEM:ANALYTICS_BACKFILL", clock.GetUtcNow(),
            claims, encryptor, message, ct);
        await invoices.SaveInvoiceChangesAsync(invoice, null, message, journal, ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
