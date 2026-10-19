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
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services;

namespace Dprental.PaymentService.Infrastructure.Repositories;

public sealed class EfSettlementLedger : ISettlementLedger
{
    public const string SettlementOperationType = "SettleRentalItem";

    private sealed record StoredReceipt(Guid SettlementId, string SettlementRef);

    // Bọc kết quả để không phụ thuộc Result<T> là class hay struct.
    private sealed class ExistingSettlement
    {
        public ExistingSettlement(Result<SettlementWriteResult> outcome) => Outcome = outcome;
        public Result<SettlementWriteResult> Outcome { get; }
    }

    private readonly PaymentServiceDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPaymentDataEncryptor _encryptor;

    public EfSettlementLedger(PaymentServiceDbContext db, TimeProvider clock, IPaymentDataEncryptor encryptor)
    {
        _db = db;
        _clock = clock;
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
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

        // Bảo vệ ở tầng ghi DB: không ghi sổ cho hóa đơn chưa thu tiền, dù caller đã kiểm tra.
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
            _db.RentalItemSettlements.Add(record);            // kéo theo Entries và RefundRequirement
            _db.OperationReceipts.Add(receipt);

            Guid operationId = Guid.NewGuid();
            Guid settlementEventId = Guid.NewGuid();

            OutboxMessage? outbox = null;
            if (record.Status == SettlementState.Completed)   // không còn gì để hoàn: đóng tài chính ngay
            {
                outbox = SettlementOutboxFactory.CreateRentalSettled(
                    record, item.Invoice, refundRef: null, completedAt: now);
                outbox.LinkAttestation(settlementEventId, operationId);
                _db.OutboxMessages.Add(outbox);
            }

            var settlementJournal = JournalEntryFactory.CreateEntry(
                operationId: operationId,
                streamId: record.SettlementRef,
                streamType: JournalStreamTypes.RentalSettlement,
                version: 1,
                eventId: settlementEventId,
                eventType: JournalEventTypes.RentalSettled,
                amount: record.FinalDeductionAmount + record.DepositRefundDue,
                currency: item.Invoice.Currency,
                status: record.Status == SettlementState.Completed ? "COMPLETED" : "MANUAL_REFUND_REQUIRED",
                confirmationSource: JournalConfirmationSources.System,
                actor: "SYSTEM",
                timestampUtc: now,
                previousHash: CanonicalJournalHasher.GenesisHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["settlementRef"] = record.SettlementRef,
                    ["invoiceRef"] = item.Invoice.InvoiceRef,
                    ["paymentRef"] = item.Invoice.PaymentRef,
                    ["rentalItemId"] = settlement.RentalItemId.ToString("D"),
                    ["rentalOrderId"] = item.Invoice.RentalOrderId.ToString("D"),
                    ["supplierId"] = settlement.SupplierId.ToString("D")
                },
                snapshot: new Dictionary<string, object?>
                {
                    ["rentalAmount"] = record.RentalAmount,
                    ["depositAmount"] = record.DepositAmount,
                    ["finalDeductionAmount"] = record.FinalDeductionAmount,
                    ["depositRefundDue"] = record.DepositRefundDue,
                    ["platformFeeAmount"] = record.PlatformFeeAmount,
                    ["supplierPayable"] = record.SupplierPayable,
                    ["status"] = (int)record.Status
                },
                outboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(outbox?.Payload),
                outboxMessageId: outbox?.MessageId,
                paymentRef: item.Invoice.PaymentRef,
                invoiceRef: item.Invoice.InvoiceRef,
                rentalOrderId: item.Invoice.RentalOrderId,
                rentalItemId: settlement.RentalItemId,
                encryptor: _encryptor);
            _db.TransactionJournals.Add(settlementJournal);

            if (record.RefundRequirement is not null)
            {
                var refundJournal = JournalEntryFactory.CreateEntry(
                    operationId: operationId,
                    streamId: record.RefundRequirement.RequirementRef,
                    streamType: JournalStreamTypes.ManualRefund,
                    version: 1,
                    eventId: Guid.NewGuid(),
                    eventType: JournalEventTypes.RefundRequired,
                    amount: record.RefundRequirement.Amount,
                    currency: item.Invoice.Currency,
                    status: "PENDING_CONFIRMATION",
                    confirmationSource: JournalConfirmationSources.System,
                    actor: "SYSTEM",
                    timestampUtc: now,
                    previousHash: CanonicalJournalHasher.GenesisHash,
                    relatedEntities: new Dictionary<string, string?>
                    {
                        ["requirementRef"] = record.RefundRequirement.RequirementRef,
                        ["settlementRef"] = record.SettlementRef,
                        ["rentalItemId"] = settlement.RentalItemId.ToString("D")
                    },
                    snapshot: new Dictionary<string, object?>
                    {
                        ["amount"] = record.RefundRequirement.Amount,
                        ["status"] = (int)record.RefundRequirement.Status
                    },
                    paymentRef: item.Invoice.PaymentRef,
                    invoiceRef: item.Invoice.InvoiceRef,
                    rentalOrderId: item.Invoice.RentalOrderId,
                    rentalItemId: settlement.RentalItemId,
                    encryptor: _encryptor);
                _db.TransactionJournals.Add(refundJournal);
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Success(new SettlementWriteResult(
                record.Id, record.SettlementRef, SettlementWriteOutcome.Created,
                record.Status, record.RefundRequirement?.RequirementRef));
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Request đồng thời đã thắng: rollback rồi tra lại để trả replay hoặc conflict.
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            var winner = await ResolveExistingAsync(settlement, fingerprint, ct);
            if (winner is not null) return winner.Outcome;

            return Error.Conflict("Payment.SettlementConflict", "Settlement xung đột với bản ghi đã tồn tại.");
        }
    }

    private async Task<ExistingSettlement?> ResolveExistingAsync(
        RentalItemSettlement s, string fingerprint, CancellationToken ct)
    {
        var receipt = await FindReceiptAsync(s.OperationKey, ct);

        if (receipt is null)
        {
            // Không thấy receipt nhưng có thể một request cùng key vừa commit xen giữa hai lần đọc.
            // Chỉ báo "khác key" khi settlement hiện có thật sự mang key khác.
            var otherKey = await _db.RentalItemSettlements.AsNoTracking()
                .Where(x => x.RentalItemId == s.RentalItemId)
                .Select(x => x.OperationKey)
                .FirstOrDefaultAsync(ct);

            if (otherKey is null) return null;   // chưa có settlement nào cho item này

            if (!string.Equals(otherKey, s.OperationKey, StringComparison.Ordinal))
            {
                return new ExistingSettlement(Error.Conflict("Payment.SettlementKeyConflict",
                    "RentalItem này đã có settlement với operation key khác."));
            }

            // Cùng key: settlement và receipt được commit trong một transaction nên receipt đã tồn tại.
            receipt = await FindReceiptAsync(s.OperationKey, ct);
            if (receipt is null)
            {
                return new ExistingSettlement(Error.Conflict("Payment.SettlementReceiptMissing",
                    "Settlement tồn tại nhưng thiếu receipt tương ứng."));
            }
        }

        return new ExistingSettlement(await ResolveFromReceiptAsync(receipt, fingerprint, ct));
    }

    private Task<OperationReceipt?> FindReceiptAsync(string operationKey, CancellationToken ct) =>
        _db.OperationReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.OperationKey == operationKey, ct);

    private async Task<Result<SettlementWriteResult>> ResolveFromReceiptAsync(
        OperationReceipt receipt, string fingerprint, CancellationToken ct)
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

        var stored = JsonSerializer.Deserialize<StoredReceipt>(receipt.ResultJson);
        if (stored is null)
        {
            return Error.Conflict("Payment.SettlementReceiptCorrupted", "Receipt settlement không đọc được.");
        }

        // Replay trả trạng thái HIỆN TẠI, không phải trạng thái lúc tạo.
        var current = await _db.RentalItemSettlements.AsNoTracking()
            .Include(x => x.RefundRequirement)
            .SingleAsync(x => x.Id == stored.SettlementId, ct);

        return Result.Success(new SettlementWriteResult(
            current.Id, current.SettlementRef, SettlementWriteOutcome.Replayed,
            current.Status, current.RefundRequirement?.RequirementRef));
    }

    private static string ComputeFingerprint(RentalItemSettlement s)
    {
        var inv = CultureInfo.InvariantCulture;
        var canonical = string.Join('|',
            s.OperationKey,
            s.InvoiceId.ToString("D"), s.InvoiceItemId.ToString("D"),
            s.RentalItemId.ToString("D"), s.SupplierId.ToString("D"),
            s.DecisionRef,
            s.RentalAmount.ToString("0", inv), s.DepositAmount.ToString("0", inv),
            s.FinalDeductionAmount.ToString("0", inv), s.DepositRefundDue.ToString("0", inv),
            s.PlatformFeeRate.ToString("0.0000", inv), s.PlatformFeeAmount.ToString("0", inv),
            s.SupplierPayable.ToString("0", inv));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
