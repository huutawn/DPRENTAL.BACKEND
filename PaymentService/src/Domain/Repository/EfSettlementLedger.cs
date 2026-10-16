using System.Globalization;
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

public sealed class EfSettlementLedger : ISettlementLedger
{
    // Phân biệt với các receipt của lệnh hóa đơn (OperationKey unique toàn bảng, tối đa 32 ký tự type).
    public const string SettlementOperationType = "SettleRentalItem";

    private readonly PaymentServiceDbContext _db;
    private readonly TimeProvider _clock;

    public EfSettlementLedger(PaymentServiceDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Result<SettlementWriteResult>> RecordSettlementAsync(
        RentalItemSettlement settlement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var fingerprint = ComputeFingerprint(settlement);

        // 1. Retry thường gặp: đã có receipt hoặc đã có settlement cho item này.
        var existing = await ResolveExistingAsync(settlement, fingerprint, ct);
        if (existing is not null) return existing;

        // 2. Đối chiếu snapshot trong DB Payment (không đọc Rental DB).
        var item = await _db.InvoiceItems.AsNoTracking()
            .Include(x => x.Invoice)
            .SingleOrDefaultAsync(x => x.Id == settlement.InvoiceItemId, ct);

        if (item?.Invoice is null)
        {
            return Error.NotFound("Payment.InvoiceItemNotFound", "Không tìm thấy InvoiceItem của settlement.");
        }

        if (item.InvoiceId != settlement.InvoiceId || item.RentalItemId != settlement.RentalItemId ||
            item.SupplierId != settlement.SupplierId || item.RentalAmount != settlement.RentalAmount ||
            item.DepositAmount != settlement.DepositAmount)
        {
            return Error.Conflict("Payment.SettlementSnapshotMismatch", "Settlement không khớp snapshot InvoiceItem.");
        }

        if (item.Invoice.Status != InvoiceStatus.Paid)
        {
            return Error.Conflict("Payment.InvoiceNotPaid", "Hóa đơn chưa được thanh toán.");
        }

        // 3. Record + 3 bút toán + receipt: một SaveChanges trong một transaction.
        var now = _clock.GetUtcNow();
        var record = RentalItemSettlementRecord.From(settlement, now);
        var created = new SettlementWriteResult(record.Id, record.SettlementRef, SettlementWriteOutcome.Created);
        var receipt = OperationReceipt.Create(
            settlement.OperationKey, SettlementOperationType, fingerprint,
            JsonSerializer.Serialize(created),
            item.Invoice.InvoiceRef, item.Invoice.RentalOrderId, now);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.RentalItemSettlements.Add(record);   // Entries đi theo navigation
            _db.OperationReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success(created);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Request đồng thời đã thắng: rollback rồi tra lại để trả replay hoặc conflict.
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var winner = await ResolveExistingAsync(settlement, fingerprint, ct);
            return winner ?? Error.Conflict("Payment.SettlementConflict",
                "Settlement xung đột với bản ghi đã tồn tại.");
        }
    }

    private async Task<Result<SettlementWriteResult>?> ResolveExistingAsync(
        RentalItemSettlement s, string fingerprint, CancellationToken ct)
    {
        var receipt = await _db.OperationReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.OperationKey == s.OperationKey, ct);

        if (receipt is not null)
        {
            return ResolveReceipt(receipt, fingerprint);
        }

        // Receipt có thể chưa được commit ở lần đọc trên nhưng settlement đã thấy ở lần đọc này.
        var existingSettlement = await _db.RentalItemSettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RentalItemId == s.RentalItemId, ct);

        if (existingSettlement is not null)
        {
            if (existingSettlement.OperationKey == s.OperationKey)
            {
                var committedReceipt = await _db.OperationReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.OperationKey == s.OperationKey, ct);
                if (committedReceipt is not null)
                {
                    return ResolveReceipt(committedReceipt, fingerprint);
                }
            }

            return Error.Conflict("Payment.SettlementKeyConflict",
                "RentalItem này đã có settlement với operation key khác.");
        }

        return null;
    }

    private static Result<SettlementWriteResult> ResolveReceipt(OperationReceipt receipt, string fingerprint)
    {
        if (receipt.OperationType != SettlementOperationType)
        {
            return Error.Conflict("Payment.OperationKeyTypeConflict",
                "Operation key đã được dùng cho loại thao tác khác.");
        }

        if (receipt.PayloadFingerprint != fingerprint)
        {
            return Error.Conflict("Payment.SettlementPayloadConflict",
                "Operation key đã dùng với nội dung settlement khác.");
        }

        var stored = JsonSerializer.Deserialize<SettlementWriteResult>(receipt.ResultJson);
        if (stored is null)
        {
            return Error.Conflict("Payment.SettlementReceiptCorrupted", "Receipt settlement không đọc được.");
        }

        return Result.Success(stored with { Outcome = SettlementWriteOutcome.Replayed });
    }

    private static string ComputeFingerprint(RentalItemSettlement s)
    {
        var inv = CultureInfo.InvariantCulture;
        // Số tiền là VND nguyên nên format "0" để không phụ thuộc scale của decimal.
        var canonical = string.Join('|',
            s.OperationKey,
            s.InvoiceId.ToString("D"), s.InvoiceItemId.ToString("D"),
            s.RentalItemId.ToString("D"), s.SupplierId.ToString("D"),
            s.DecisionRef,
            s.RentalAmount.ToString("0", inv), s.DepositAmount.ToString("0", inv),
            s.FinalDeductionAmount.ToString("0", inv), s.DepositRefundDue.ToString("0", inv),
            s.PlatformFeeRate.ToString("0.0000", inv), s.PlatformFeeAmount.ToString("0", inv),
            s.SupplierPayable.ToString("0", inv));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(); // 64 ký tự
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
