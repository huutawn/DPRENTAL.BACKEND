using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.Entities;

[Table("rental_item_settlements")]
public sealed class RentalItemSettlementRecord
{
    [Key]
    public Guid Id { get; private set; }

    [Required, MaxLength(64)]
    public string SettlementRef { get; private set; } = default!;

    [Required, MaxLength(64)]
    public string OperationKey { get; private set; } = default!;

    [Required] public Guid InvoiceId { get; private set; }
    [Required] public Guid InvoiceItemId { get; private set; }
    [Required] public Guid RentalItemId { get; private set; }
    [Required] public Guid SupplierId { get; private set; }

    [Required, MaxLength(256)]
    public string DecisionRef { get; private set; } = default!;

    [Column(TypeName = "numeric(18,0)")] public decimal RentalAmount { get; private set; }
    [Column(TypeName = "numeric(18,0)")] public decimal DepositAmount { get; private set; }
    [Column(TypeName = "numeric(18,0)")] public decimal FinalDeductionAmount { get; private set; }
    [Column(TypeName = "numeric(18,0)")] public decimal DepositRefundDue { get; private set; }
    [Column(TypeName = "numeric(5,4)")] public decimal PlatformFeeRate { get; private set; }
    [Column(TypeName = "numeric(18,0)")] public decimal PlatformFeeAmount { get; private set; }
    [Column(TypeName = "numeric(18,0)")] public decimal SupplierPayable { get; private set; }

    [Required]
    public SettlementState Status { get; private set; }

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public List<SettlementLedgerEntry> Entries { get; private set; } = new();

    public ManualRefundRequirement? RefundRequirement { get; private set; }

    private RentalItemSettlementRecord() { }

    public SupplierPayout? Payout { get; private set; }

    public static RentalItemSettlementRecord From(RentalItemSettlement s, Invoice invoice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (string.IsNullOrWhiteSpace(invoice.PaymentRef))
            throw new InvalidOperationException("Paid invoice must have a payment reference.");

        var id = Guid.NewGuid();
        var record = new RentalItemSettlementRecord
        {
            Id = id,
            SettlementRef = $"STL-{id:N}",
            OperationKey = s.OperationKey,
            InvoiceId = s.InvoiceId,
            InvoiceItemId = s.InvoiceItemId,
            RentalItemId = s.RentalItemId,
            SupplierId = s.SupplierId,
            DecisionRef = s.DecisionRef,
            RentalAmount = s.RentalAmount,
            DepositAmount = s.DepositAmount,
            FinalDeductionAmount = s.FinalDeductionAmount,
            DepositRefundDue = s.DepositRefundDue,
            PlatformFeeRate = s.PlatformFeeRate,
            PlatformFeeAmount = s.PlatformFeeAmount,
            SupplierPayable = s.SupplierPayable,
            CreatedAt = now
        };

        // Luôn đủ 3 bút toán. FinalDeductionAmount KHÔNG phải khoản thứ tư:
        // nó đã nằm trong SupplierPayable và đã làm giảm DepositRefundDue.
        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.SupplierPayable, s.SupplierPayable, now));
        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.PlatformFee, s.PlatformFeeAmount, now));
        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.DepositRefundDue, s.DepositRefundDue, now));

        if (s.SupplierPayable > 0)
        {
            record.Payout = SupplierPayout.CreatePending(
                id, s.RentalItemId, s.SupplierId,
                s.SupplierPayable, invoice.Currency,
                destinationRef: null, now);
        }

        if (s.DepositRefundDue > 0)
        {
            // Còn tiền cọc phải hoàn: chưa đóng tài chính, tạo đúng một yêu cầu hoàn thủ công.
            record.Status = SettlementState.PendingManualRefund;
            record.RefundRequirement = ManualRefundRequirement.CreatePending(
                id, s.InvoiceId, s.RentalItemId, invoice.RenterId,
                invoice.PaymentRef, s.DepositRefundDue, now);
        }
        else
        {
            record.Status = SettlementState.Completed;
            record.CompletedAt = now;
        }

        return record;
    }

    public Result CompleteAfterRefund(ManualRefundRequirement refund, DateTimeOffset now)
    {
        if (refund.SettlementId != Id || DepositRefundDue <= 0 ||
            refund.Status != ManualRefundStatus.Completed)
            return Error.Conflict("Payment.RefundNotCompleted", "Chưa xác nhận hoàn đủ tiền cọc cho settlement này.");

        if (Status == SettlementState.Completed) return Result.Success();

        Status = SettlementState.Completed;
        CompletedAt = now.ToUniversalTime();
        return Result.Success();
    }
}
