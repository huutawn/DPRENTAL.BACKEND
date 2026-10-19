using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.PaymentService.Domain.Repository;

public sealed class EfManualRefundRepository(
    PaymentServiceDbContext db, TimeProvider clock) : IManualRefundRepository
{
    private const string ExternalReferenceIndex = "UX_manual_refund_requirements_ExternalTransferReference";

    public async Task<Result<ManualRefundCompletion>> CompleteAsync(
        CompleteManualRefundCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.OperationKey) ||
            string.IsNullOrWhiteSpace(command.ExternalTransferReference) ||
            string.IsNullOrWhiteSpace(command.AuditReason))
            return Error.Validation("Payment.InvalidRefundConfirmation", "Thông tin xác nhận hoàn cọc không hợp lệ.");

        var operationKey = command.OperationKey.Trim();
        var reference = command.ExternalTransferReference.Trim();
        var reason = command.AuditReason.Trim();

        if (command.RequirementId == Guid.Empty || command.AdminId == Guid.Empty ||
            operationKey.Length is 0 or > 64 || reference.Length is 0 or > 256 || reason.Length is 0 or > 500 ||
            operationKey.Any(char.IsControl) || reference.Any(char.IsControl) || reason.Any(char.IsControl))
            return Error.Validation("Payment.InvalidRefundConfirmation", "Thông tin xác nhận hoàn cọc không hợp lệ.");

        var fingerprint = Fingerprint(command.RequirementId, command.AdminId, reference, reason);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Giữ khóa hàng này đến hết commit (kể cả outbox): các xác nhận đồng thời xếp hàng tại đây.
            var refund = (await db.ManualRefundRequirements
                .FromSqlInterpolated($"SELECT * FROM manual_refund_requirements WHERE \"Id\" = {command.RequirementId} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (refund is null)
                return Error.NotFound("Payment.ManualRefundNotFound", "Không tìm thấy yêu cầu hoàn cọc.");

            var settlement = await db.RentalItemSettlements
                .SingleAsync(x => x.Id == refund.SettlementId, cancellationToken);

            // Audit theo OperationKey phải được xét TRƯỚC trạng thái: retry sau khi đã hoàn tất vẫn là replay.
            var prior = await db.ManualRefundOperationAudits.AsNoTracking()
                .SingleOrDefaultAsync(a => a.OperationKey == operationKey, cancellationToken);
            if (prior is not null)
                return ReplayOrConflict(prior, refund, settlement, fingerprint);

            // Key mới trên refund đã hoàn tất (kể cả dữ liệu cũ không có audit): 409, không phát RentalSettled.
            if (refund.Status == ManualRefundStatus.Completed)
                return Error.Conflict("Payment.RefundAlreadyCompleted", "Yêu cầu hoàn cọc đã được xác nhận hoàn tất.");

            var usedElsewhere = await db.ManualRefundRequirements.AsNoTracking()
                .AnyAsync(x => x.ExternalTransferReference == reference && x.Id != refund.Id, cancellationToken);
            if (usedElsewhere)
                return Error.Conflict("Payment.ExternalReferenceConflict",
                    "Mã chuyển khoản đã được dùng cho yêu cầu hoàn khác.");

            var now = clock.GetUtcNow();

            var completed = refund.Complete(reference, command.AdminId, now);
            if (completed.IsFailure) return completed.Error;

            var closed = settlement.CompleteAfterRefund(refund, now);
            if (closed.IsFailure) return closed.Error;

            var invoice = await db.Invoices.AsNoTracking()
                .SingleAsync(x => x.Id == settlement.InvoiceId, cancellationToken);

            // Refund + settlement + audit + RentalSettled outbox: một SaveChanges, một transaction.
            db.ManualRefundOperationAudits.Add(ManualRefundOperationAudit.Create(
                operationKey, fingerprint, refund, command.AdminId, reference, reason, now));
            db.OutboxMessages.Add(SettlementOutboxFactory.CreateRentalSettled(
                settlement, invoice, refund.ExternalTransferReference, now));

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success(ToCompletion(refund, settlement, isReplay: false));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await ResolveAfterRaceAsync(command.RequirementId, operationKey, fingerprint, ex, cancellationToken);
        }
    }

    private static Result<ManualRefundCompletion> ReplayOrConflict(
        ManualRefundOperationAudit prior, ManualRefundRequirement refund,
        RentalItemSettlementRecord settlement, string fingerprint)
    {
        if (prior.OperationType != ManualRefundOperationTypes.CompleteManualRefund)
            return Error.Conflict("Payment.OperationKeyTypeConflict",
                "Operation key đã được dùng cho loại thao tác khác.");

        if (prior.RefundRequirementId != refund.Id || prior.PayloadFingerprint != fingerprint)
            return Error.Conflict("Payment.ManualRefundPayloadConflict",
                "Operation key đã dùng với nội dung hoặc yêu cầu hoàn khác.");

        // Replay: không audit mới, không outbox mới.
        return Result.Success(ToCompletion(refund, settlement, isReplay: true));
    }

    private async Task<Result<ManualRefundCompletion>> ResolveAfterRaceAsync(
        Guid requirementId, string operationKey, string fingerprint,
        DbUpdateException ex, CancellationToken cancellationToken)
    {
        var prior = await db.ManualRefundOperationAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OperationKey == operationKey, cancellationToken);

        if (prior is not null)
        {
            var refund = await db.ManualRefundRequirements.AsNoTracking()
                .Include(x => x.Settlement)
                .SingleOrDefaultAsync(x => x.Id == requirementId, cancellationToken);

            if (refund?.Settlement is null)
                return Error.NotFound("Payment.ManualRefundNotFound", "Không tìm thấy yêu cầu hoàn cọc.");

            return ReplayOrConflict(prior, refund, refund.Settlement, fingerprint);
        }

        if ((ex.InnerException as PostgresException)?.ConstraintName == ExternalReferenceIndex)
            return Error.Conflict("Payment.ExternalReferenceConflict",
                "Mã chuyển khoản đã được dùng cho yêu cầu hoàn khác.");

        return Error.Conflict("Payment.ManualRefundOperationConflict",
            "Thao tác xác nhận hoàn cọc xung đột với bản ghi đã tồn tại.");
    }

    private static ManualRefundCompletion ToCompletion(
        ManualRefundRequirement refund, RentalItemSettlementRecord settlement, bool isReplay) =>
        new(refund.Id, refund.RequirementRef, refund.RentalItemId,
            settlement.SettlementRef, refund.Amount, refund.ExternalTransferReference!,
            refund.CompletedAt!.Value, refund.CompletedBy!.Value, isReplay);

    // JSON array: các trường văn bản tự do không thể "dịch ranh giới" giữa hai trường.
    private static string Fingerprint(Guid requirementId, Guid adminId, string reference, string reason)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            ManualRefundOperationTypes.CompleteManualRefund,
            requirementId.ToString("D"), adminId.ToString("D"), reference, reason
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
