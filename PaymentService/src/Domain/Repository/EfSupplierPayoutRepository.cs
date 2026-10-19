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

public sealed class EfSupplierPayoutRepository : ISupplierPayoutRepository
{
    private const string ExternalReferenceIndex = "UX_supplier_payouts_ExternalTransferReference";

    private readonly PaymentServiceDbContext _db;
    private readonly TimeProvider _clock;

    public EfSupplierPayoutRepository(PaymentServiceDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<Result<SupplierPayoutWriteResult>> AttachDestinationAsync(
        AttachPayoutDestinationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        const string type = FinancialOperationTypes.AttachPayoutDestination;
        var fingerprint = Fingerprint(type, command.PayoutId, command.AdminId,
            $"{command.DestinationRef}:{command.Version}", command.AuditReason);
        SupplierPayoutDestination? selected = null;

        return ExecuteAsync(
            command.PayoutId, command.OperationKey, type, fingerprint,
            validate: async (p, token) =>
            {
                selected = await FindDestinationAsync(command.DestinationRef, command.Version, token);
                if (selected is null)
                    return Error.NotFound("Payment.PayoutDestinationNotFound", "Không tìm thấy tài khoản nhận tiền đã xác minh.");
                if (selected.SupplierId != p.SupplierId)
                    return Error.Conflict("Payment.PayoutDestinationSupplierMismatch", "Tài khoản nhận tiền không thuộc supplier của payout.");
                return p.ValidateCanAttachDestination(selected.Id);
            },
            mutate: (p, _) => p.AttachDestination(selected!),
            buildAudit: (p, now, previousRef) => FinancialOperationAudit.Create(
                command.OperationKey, type, fingerprint, p, command.AdminId, command.AuditReason, now, previousRef),
            ct);
    }

    public Task<Result<SupplierPayoutWriteResult>> CompleteAsync(
        CompleteSupplierPayoutCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        const string type = FinancialOperationTypes.CompleteSupplierPayout;
        var fingerprint = Fingerprint(type, command.PayoutId, command.AdminId, command.ExternalTransferReference, command.AuditReason);

        return ExecuteAsync(
            command.PayoutId, command.OperationKey, type, fingerprint,
            validate: async (p, token) =>
            {
                if (p.ValidateCanComplete() is { } blocked) return blocked;

                var destination = await _db.SupplierPayoutDestinations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == p.DestinationId, token);
                if (destination is null || destination.SupplierId != p.SupplierId)
                    return Error.Conflict("Payment.PayoutDestinationInvalid", "Payout destination không còn hợp lệ.");

                var usedElsewhere = await _db.SupplierPayouts.AsNoTracking()
                    .AnyAsync(x => x.ExternalTransferReference == command.ExternalTransferReference && x.Id != p.Id, token);
                if (usedElsewhere)
                    return Error.Conflict("Payment.ExternalReferenceConflict",
                        "Mã chuyển khoản đã được dùng cho payout khác.");

                return null;
            },
            mutate: (p, now) => p.Complete(command.ExternalTransferReference, command.AdminId, now),
            buildAudit: (p, now, previousRef) => FinancialOperationAudit.Create(
                command.OperationKey, type, fingerprint, p, command.AdminId, command.AuditReason, now),
            ct);
    }

    private async Task<Result<SupplierPayoutWriteResult>> ExecuteAsync(
        Guid payoutId, string operationKey, string operationType, string fingerprint,
        Func<SupplierPayout, CancellationToken, Task<Error?>> validate,
        Action<SupplierPayout, DateTimeOffset> mutate,
        Func<SupplierPayout, DateTimeOffset, string?, FinancialOperationAudit> buildAudit,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var payout = await LockPayoutAsync(payoutId, ct);
            if (payout is null)
                return Error.NotFound("Payment.PayoutNotFound", "Không tìm thấy payout.");

            // Replay/conflict phải xét trước điều kiện trạng thái: retry sau khi đã COMPLETED vẫn phải ra replay.
            var prior = await FindAuditAsync(operationKey, ct);
            if (prior is not null)
                return ReplayOrConflict(prior, payout, operationType, fingerprint);

            if (await validate(payout, ct) is { } rejected)
                return rejected;                       // thoát khỏi using => rollback, nhả khóa

            var now = _clock.GetUtcNow();
            var previousRef = payout.DestinationRef;
            mutate(payout, now);
            _db.FinancialOperationAudits.Add(buildAudit(payout, now, previousRef));

            await _db.SaveChangesAsync(ct);            // payout + audit: một transaction
            await tx.CommitAsync(ct);

            return Result.Success(Map(payout, isReplay: false));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return await ResolveAfterRaceAsync(payoutId, operationKey, operationType, fingerprint, ex, ct);
        }
    }

    private async Task<SupplierPayout?> LockPayoutAsync(Guid payoutId, CancellationToken ct)
    {
        // ToListAsync thay vì SingleOrDefaultAsync để EF không bọc thêm subquery lên câu FOR UPDATE.
        var rows = await _db.SupplierPayouts
            .FromSqlInterpolated($"SELECT * FROM supplier_payouts WHERE \"Id\" = {payoutId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    private Task<SupplierPayoutDestination?> FindDestinationAsync(string reference, int version, CancellationToken ct) =>
        _db.SupplierPayoutDestinations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DestinationRef == reference && x.Version == version, ct);

    private Task<FinancialOperationAudit?> FindAuditAsync(string operationKey, CancellationToken ct) =>
        _db.FinancialOperationAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.OperationKey == operationKey, ct);

    private static Result<SupplierPayoutWriteResult> ReplayOrConflict(
        FinancialOperationAudit prior, SupplierPayout payout, string operationType, string fingerprint)
    {
        if (prior.OperationType != operationType)
            return Error.Conflict("Payment.OperationKeyTypeConflict",
                "Operation key đã được dùng cho loại thao tác khác.");

        if (prior.PayoutId != payout.Id || prior.PayloadFingerprint != fingerprint)
            return Error.Conflict("Payment.PayoutPayloadConflict",
                "Operation key đã dùng với nội dung hoặc payout khác.");

        // Replay trả trạng thái HIỆN TẠI của payout, không tạo audit mới.
        return Result.Success(Map(payout, isReplay: true));
    }

    private async Task<Result<SupplierPayoutWriteResult>> ResolveAfterRaceAsync(
        Guid payoutId, string operationKey, string operationType, string fingerprint,
        DbUpdateException ex, CancellationToken ct)
    {
        var prior = await FindAuditAsync(operationKey, ct);
        if (prior is not null)
        {
            var payout = await _db.SupplierPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == payoutId, ct);
            if (payout is null)
                return Error.NotFound("Payment.PayoutNotFound", "Không tìm thấy payout.");

            return ReplayOrConflict(prior, payout, operationType, fingerprint);
        }

        if (ConstraintName(ex) == ExternalReferenceIndex)
            return Error.Conflict("Payment.ExternalReferenceConflict", "Mã chuyển khoản đã được dùng cho payout khác.");

        return Error.Conflict("Payment.PayoutOperationConflict", "Thao tác payout xung đột với bản ghi đã tồn tại.");
    }

    private static SupplierPayoutWriteResult Map(SupplierPayout p, bool isReplay) =>
        new(p.Id, p.PayoutRef, p.Status, p.DestinationRef, p.ExternalTransferReference,
            p.CompletedAt, p.CompletedBy, isReplay);

    // JSON array để các trường văn bản tự do không thể "dịch ranh giới" giữa hai trường.
    private static string Fingerprint(string type, Guid payoutId, Guid adminId, string value, string reason)
    {
        var canonical = JsonSerializer.Serialize(
            new[] { type, payoutId.ToString("D"), adminId.ToString("D"), value, reason });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string? ConstraintName(DbUpdateException ex) =>
        (ex.InnerException as PostgresException)?.ConstraintName;
}
