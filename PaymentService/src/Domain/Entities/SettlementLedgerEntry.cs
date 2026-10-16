using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

public enum SettlementEntryType
{
    SupplierPayable = 1,
    PlatformFee = 2,
    DepositRefundDue = 3
}

[Table("settlement_ledger_entries")]
public sealed class SettlementLedgerEntry
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    public Guid SettlementId { get; private set; }

    [Required]
    public SettlementEntryType EntryType { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal Amount { get; private set; }

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    [ForeignKey(nameof(SettlementId))]
    public RentalItemSettlementRecord? Settlement { get; private set; }

    private SettlementLedgerEntry() { }

    internal static SettlementLedgerEntry Create(
        Guid settlementId, SettlementEntryType type, decimal amount, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            SettlementId = settlementId,
            EntryType = type,
            Amount = amount,
            CreatedAt = now
        };
}