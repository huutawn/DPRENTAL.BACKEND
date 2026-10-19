using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class EfSettlementLedger : ISettlementLedger
{
    public const string SettlementOperationType = "SettleRentalItem";

    private sealed record StoredReceipt(Guid SettlementId, string SettlementRef);

    private sealed class ExistingSettlement
    {
        public ExistingSettlement(Result<SettlementWriteResult> outcome) => Outcome = outcome;
        public Result<SettlementWriteResult> Outcome { get; }
    }

    private readonly PaymentServiceDbContext _db;
    private readonly TimeProvider _clock;

    public EfSettlementLedger(PaymentServiceDbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<SettlementWriteResult>> RecordSettlementAsync(
        RentalItemSettlement settlement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var fingerprint = ComputeFingerprint(settlement);

        var existing = await ResolveExistingAsync(settlement, fingerprint, ct);
        if (existing is not null) return existing.Outcome;

        var item = await _db.InvoiceItems.AsNoTracking()
            .Include(x => x.Invoice)
            .SingleOrDefaultAsync(x => x.Id == settlement.InvoiceItemId, ct);

        if (item?.Invoice is null)
        {
            return Error.NotFound("Payment.InvoiceItemNotFound", "Không tìm thấy InvoiceItem của settlement.");
        }

        if (!string.Equals(item.Invoice.Status, InvoiceStatus.Paid, StringComparison.Ordinal))
        {
            return Error.Conflict("Payment.InvoiceNotPaid", "Hóa đơn chưa được thanh toán nên chưa thể settle.");
        }

        if (item.InvoiceId != settlement.InvoiceId || item.RentalItemId != settlement.RentalItemId ||
            item.SupplierId != settlement.SupplierId || item.RentalAmount != settlement.RentalAmount ||
            item.DepositAmount != settlement.DepositAmount)
        {
            return Error.Conflict("Payment.SettlementSnapshotMismatch", "Settlement không khớp snapshot InvoiceItem.");
        }

        var now = _clock.GetUtcNow();
        var record = RentalItemSettlementRecord.From(settlement, item.Invoice, now);
        var receipt = OperationReceipt.Create(
            settlement.OperationKey, SettlementOperationType, fingerprint,
            JsonSerializer.Serialize(new StoredReceipt(record.Id, record.SettlementRef)),
            item.Invoice.InvoiceRef, item.Invoice.RentalOrderId, now);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.RentalItemSettlements.Add(record);
            if (record.RefundRequirement is not null)
            {
                _db.ManualRefundRequirements.Add(record.RefundRequirement);
            }
            _db.OperationReceipts.Add(receipt);

            if (record.Status == SettlementState.Completed)
            {
                var outbox = SettlementOutboxFactory.CreateRentalSettled(
                    record, item.Invoice, refundRef: null, completedAt: now);
                _db.OutboxMessages.Add(outbox);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success(new SettlementWriteResult(
                record.Id, record.SettlementRef, SettlementWriteOutcome.Created,
                record.Status, record.RefundRequirement?.RequirementRef));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var replay = await ResolveExistingAsync(settlement, fingerprint, ct);
            if (replay is not null) return replay.Outcome;

            throw;
        }
    }

    private async Task<ExistingSettlement?> ResolveExistingAsync(
        RentalItemSettlement settlement, string fingerprint, CancellationToken ct)
    {
        var existingReceipt = await _db.OperationReceipts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationKey == settlement.OperationKey, ct);

        if (existingReceipt is not null)
        {
            if (!string.Equals(existingReceipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new(Error.Conflict(
                    "DUPLICATE_OPERATION_KEY",
                    "OperationKey đã được sử dụng với nội dung quyết định khác."));
            }

            StoredReceipt? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<StoredReceipt>(existingReceipt.ResultJson);
            }
            catch (JsonException) { }

            if (parsed is null)
            {
                return new(Error.Failure(
                    "Payment.CorruptedReceipt", "Không thể đọc dữ liệu settlement đã lưu."));
            }

            var existingRecord = await _db.RentalItemSettlements.AsNoTracking()
                .Include(x => x.RefundRequirement)
                .SingleOrDefaultAsync(x => x.Id == parsed.SettlementId, ct);

            if (existingRecord is null)
            {
                return new(Error.NotFound(
                    "Payment.SettlementRecordMissing", "Không tìm thấy bản ghi settlement của receipt."));
            }

            return new(Result.Success(new SettlementWriteResult(
                existingRecord.Id,
                existingRecord.SettlementRef,
                SettlementWriteOutcome.Replayed,
                existingRecord.Status,
                existingRecord.RefundRequirement?.RequirementRef)));
        }

        var existingItemRecord = await _db.RentalItemSettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RentalItemId == settlement.RentalItemId, ct);

        if (existingItemRecord is not null)
        {
            return new(Error.Conflict(
                "DUPLICATE_SETTLEMENT",
                "RentalItem đã có quyết định settlement từ trước với OperationKey khác."));
        }

        return null;
    }

    public static string ComputeFingerprint(RentalItemSettlement s)
    {
        var payload = FormattableString.Invariant(
            $"{s.RentalItemId:D}|{s.InvoiceId:D}|{s.InvoiceItemId:D}|{s.SupplierId:D}|{s.DecisionRef.Trim()}|{s.RentalAmount:0}|{s.DepositAmount:0}|{s.FinalDeductionAmount:0}|{s.DepositRefundDue:0}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
