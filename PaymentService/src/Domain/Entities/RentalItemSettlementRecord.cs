using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

[Table("rental_item_settlements")]
public sealed class RentalItemSettlementRecord
{
    [Key]
    public Guid Id { get; private set; }

    [Required, MaxLength(64)]
    public string SettlementRef { get; private set; } = default!;

    [Required, MaxLength(64)]   // khớp OperationReceipt.OperationKey (key hiện tại dài 56 ký tự)
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
    public DateTimeOffset CreatedAt { get; private set; }

    public List<SettlementLedgerEntry> Entries { get; private set; } = new();

    private RentalItemSettlementRecord() { }

    public static RentalItemSettlementRecord From(RentalItemSettlement s, DateTimeOffset now)
    {
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
        return record;
    }
}