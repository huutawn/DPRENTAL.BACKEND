
namespace Dprental.PaymentService.Domain.Entities;

public sealed class RentalItemSettlementRecord
{

    public Guid Id { get; private set; }
    public string SettlementRef { get; private set; } = default!;
    public string OperationKey { get; private set; } = default!;
    public Guid InvoiceId { get; private set; }
    public Guid InvoiceItemId { get; private set; }
    public Guid RentalItemId { get; private set; }
    public Guid SupplierId { get; private set; }
    public string DecisionRef { get; private set; } = default!;
    public decimal RentalAmount { get; private set; }
    public decimal DepositAmount { get; private set; }
    public decimal FinalDeductionAmount { get; private set; }
    public decimal DepositRefundDue { get; private set; }
    public decimal PlatformFeeRate { get; private set; }
    public decimal PlatformFeeAmount { get; private set; }
    public decimal SupplierPayable { get; private set; }
    public SettlementState Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Revision { get; private set; } = 1;

    public List<SettlementLedgerEntry> Entries { get; private set; } = [];
    public ManualRefundRequirement? RefundRequirement { get; private set; }


    private RentalItemSettlementRecord() { }

    public static RentalItemSettlementRecord From(RentalItemSettlement s, Invoice invoice, SettlementState status, DateTimeOffset now)
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
            CreatedAt = now.ToUniversalTime()
        };

        record.Status = status;
        record.CompletedAt = status == SettlementState.Completed ? now.ToUniversalTime() : null;
        return record;
    }

    public void AssignCompleted(DateTimeOffset now)
    {
        Status = SettlementState.Completed;
        CompletedAt = now.ToUniversalTime();
        Revision++;
    }

    public void AssignRefundRequirement(ManualRefundRequirement requirement) => RefundRequirement = requirement;
}
