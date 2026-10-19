using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services;

namespace Dprental.PaymentService.Infrastructure.Repositories;

public sealed class EfManualRefundRepository(
    PaymentServiceDbContext db, TimeProvider clock, IPaymentDataEncryptor? encryptor = null) : IManualRefundRepository
{
    private readonly IPaymentDataEncryptor _encryptor = encryptor ?? new PaymentDataEncryptor(new byte[32]);
    public async Task<Result<ManualRefundCompletion>> CompleteAsync(
        Guid requirementId, string externalTransferReference, Guid adminId,
        CancellationToken cancellationToken)
    {
        if (requirementId == Guid.Empty || adminId == Guid.Empty ||
            string.IsNullOrWhiteSpace(externalTransferReference) ||
            externalTransferReference.Trim().Length > 256)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Thông tin xác nhận hoàn cọc không hợp lệ.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Keep this row locked through the outbox commit. Concurrent confirmations serialize here.
        var refund = (await db.ManualRefundRequirements
            .FromSqlInterpolated($"SELECT * FROM manual_refund_requirements WHERE \"Id\" = {requirementId} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (refund is null)
            return Error.NotFound("Payment.ManualRefundNotFound", "Không tìm thấy yêu cầu hoàn cọc.");

        var now = clock.GetUtcNow();
        var completed = refund.Complete(externalTransferReference, adminId, now);
        if (completed.IsFailure) return completed.Error;

        var settlement = await db.RentalItemSettlements
            .SingleAsync(x => x.Id == refund.SettlementId, cancellationToken);

        if (completed.Value)
        {
            var closed = settlement.CompleteAfterRefund(refund, now);
            if (closed.IsFailure) return closed.Error;

            var invoice = await db.Invoices.AsNoTracking()
                .SingleAsync(x => x.Id == settlement.InvoiceId, cancellationToken);
            Guid operationId = Guid.NewGuid();
            Guid eventId = Guid.NewGuid();
            var outbox = SettlementOutboxFactory.CreateRentalSettled(
                settlement, invoice, refund.ExternalTransferReference, now);
            outbox.LinkAttestation(eventId, operationId);
            db.OutboxMessages.Add(outbox);

            var previousEntry = await db.TransactionJournals
                .Where(j => j.StreamId == refund.RequirementRef)
                .OrderByDescending(j => j.Version)
                .FirstOrDefaultAsync(cancellationToken);

            long nextVersion = (previousEntry?.Version ?? 0) + 1;
            string prevHash = previousEntry?.EntryHash ?? Dprental.PaymentService.Common.Cryptography.CanonicalJournalHasher.GenesisHash;

            var refundJournal = JournalEntryFactory.CreateEntry(
                operationId: operationId,
                streamId: refund.RequirementRef,
                streamType: JournalStreamTypes.ManualRefund,
                version: nextVersion,
                eventId: eventId,
                eventType: JournalEventTypes.RefundConfirmed,
                amount: refund.Amount,
                currency: invoice.Currency,
                status: "COMPLETED",
                confirmationSource: JournalConfirmationSources.Admin,
                actor: $"ADMIN:{adminId:D}",
                timestampUtc: now,
                previousHash: prevHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["requirementRef"] = refund.RequirementRef,
                    ["settlementRef"] = settlement.SettlementRef,
                    ["rentalItemId"] = refund.RentalItemId.ToString("D"),
                    ["externalTransferReference"] = refund.ExternalTransferReference
                },
                snapshot: new Dictionary<string, object?>
                {
                    ["amount"] = refund.Amount,
                    ["status"] = (int)refund.Status,
                    ["externalTransferReference"] = refund.ExternalTransferReference
                },
                outboxDigest: Dprental.PaymentService.Common.Cryptography.CanonicalJournalHasher.ComputeOutboxDigest(outbox.Payload),
                outboxMessageId: outbox.MessageId,
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                rentalItemId: refund.RentalItemId,
                encryptor: _encryptor);
            db.TransactionJournals.Add(refundJournal);

            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return Result.Success(new ManualRefundCompletion(
            refund.Id, refund.RequirementRef, refund.RentalItemId,
            settlement.SettlementRef, refund.Amount, refund.ExternalTransferReference!,
            refund.CompletedAt!.Value, refund.CompletedBy!.Value, !completed.Value));
    }
}
