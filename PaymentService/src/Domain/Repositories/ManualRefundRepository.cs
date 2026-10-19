using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class ManualRefundRepository : IManualRefundRepository
{
    private readonly PaymentServiceDbContext _db;
    private readonly TimeProvider _clock;

    public ManualRefundRepository(
        PaymentServiceDbContext db,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ManualRefundRequirement?> FindByIdWithRowLockAsync(Guid requirementId, CancellationToken cancellationToken)
    {
        return (await _db.ManualRefundRequirements
            .FromSqlInterpolated($"SELECT * FROM manual_refund_requirements WHERE \"Id\" = {requirementId} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
    }

    public async Task<ManualRefundCompletion?> GetCompletedReplayAsync(
        Guid requirementId, string externalTransferReference, CancellationToken cancellationToken = default)
    {
        var refund = await _db.ManualRefundRequirements
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requirementId, cancellationToken);

        if (refund is null || refund.Status != ManualRefundStatus.Completed)
        {
            return null;
        }

        if (!string.Equals(refund.ExternalTransferReference, externalTransferReference.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var settlement = await _db.RentalItemSettlements
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == refund.SettlementId, cancellationToken);

        return new ManualRefundCompletion(
            refund.Id,
            refund.RequirementRef,
            refund.RentalItemId,
            settlement?.SettlementRef ?? string.Empty,
            refund.Amount,
            refund.ExternalTransferReference!,
            refund.CompletedAt ?? refund.CreatedAt,
            refund.CompletedBy ?? Guid.Empty,
            IsReplay: true);
    }

    public async Task<Result<ManualRefundCompletion>> CompleteAsync(
        Guid requirementId,
        string externalTransferReference,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        if (requirementId == Guid.Empty || adminId == Guid.Empty ||
            string.IsNullOrWhiteSpace(externalTransferReference) ||
            externalTransferReference.Trim().Length > 256)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Thông tin xác nhận hoàn cọc không hợp lệ.");

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var refund = (await _db.ManualRefundRequirements
            .FromSqlInterpolated($"SELECT * FROM manual_refund_requirements WHERE \"Id\" = {requirementId} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (refund is null)
            return Error.NotFound("Payment.ManualRefundNotFound", "Không tìm thấy yêu cầu hoàn cọc.");

        var settlement = await _db.RentalItemSettlements
            .FromSqlInterpolated($"SELECT * FROM rental_item_settlements WHERE \"Id\" = {refund.SettlementId} FOR UPDATE")
            .SingleAsync(cancellationToken);

        if (refund.Status == ManualRefundStatus.Completed)
        {
            if (string.Equals(refund.ExternalTransferReference, externalTransferReference.Trim(), StringComparison.Ordinal))
            {
                await tx.CommitAsync(cancellationToken);
                return Result.Success(new ManualRefundCompletion(
                    refund.Id, refund.RequirementRef, refund.RentalItemId,
                    settlement.SettlementRef, refund.Amount, refund.ExternalTransferReference!,
                    refund.CompletedAt!.Value, refund.CompletedBy!.Value, IsReplay: true));
            }

            await tx.RollbackAsync(cancellationToken);
            return Error.Conflict("Payment.RefundAlreadyCompleted", "Yêu cầu hoàn cọc đã được xác nhận bằng mã chuyển khoản khác.");
        }

        var now = _clock.GetUtcNow();
        var completed = refund.Complete(externalTransferReference, adminId, now);
        if (completed.IsFailure) return completed.Error;

        if (completed.Value)
        {
            var closed = settlement.CompleteAfterRefund(refund, now);
            if (closed.IsFailure) return closed.Error;

            var invoice = await _db.Invoices.AsNoTracking()
                .SingleAsync(x => x.Id == settlement.InvoiceId, cancellationToken);
            var outbox = SettlementOutboxFactory.CreateRentalSettled(
                settlement, invoice, refund.ExternalTransferReference, now);
            _db.OutboxMessages.Add(outbox);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return Result.Success(new ManualRefundCompletion(
            refund.Id, refund.RequirementRef, refund.RentalItemId,
            settlement.SettlementRef, refund.Amount, refund.ExternalTransferReference!,
            refund.CompletedAt!.Value, refund.CompletedBy!.Value, !completed.Value));
    }
}
