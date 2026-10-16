using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repository;

public sealed class EfManualRefundRepository(
    PaymentServiceDbContext db, TimeProvider clock) : IManualRefundRepository
{
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
            db.OutboxMessages.Add(SettlementOutboxFactory.CreateRentalSettled(
                settlement, invoice, refund.ExternalTransferReference, now));
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return Result.Success(new ManualRefundCompletion(
            refund.Id, refund.RequirementRef, refund.RentalItemId,
            settlement.SettlementRef, refund.Amount, refund.ExternalTransferReference!,
            refund.CompletedAt!.Value, refund.CompletedBy!.Value, !completed.Value));
    }
}
