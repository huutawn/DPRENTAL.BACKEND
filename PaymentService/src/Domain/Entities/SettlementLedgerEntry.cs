namespace Dprental.PaymentService.Domain.Entities;

public enum SettlementEntryType
{
    SupplierPayable = 1,
    PlatformFee = 2,
    DepositRefundDue = 3
}

public sealed class SettlementLedgerEntry
{
    public Guid Id { get; private set; }
    public Guid SettlementId { get; private set; }
    public SettlementEntryType EntryType { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public RentalItemSettlementRecord Settlement { get; private set; } = null!;

    private SettlementLedgerEntry() { }

    public static SettlementLedgerEntry Create(
        Guid settlementId,
        SettlementEntryType entryType,
        decimal amount,
        DateTimeOffset createdAt)
    {
        return new SettlementLedgerEntry
        {
            Id = Guid.NewGuid(),
            SettlementId = settlementId,
            EntryType = entryType,
            Amount = amount,
            CreatedAt = createdAt.ToUniversalTime()
        };
    }
}
