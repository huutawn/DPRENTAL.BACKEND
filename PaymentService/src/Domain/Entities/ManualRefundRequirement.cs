namespace Dprental.PaymentService.Domain.Entities;

public enum ManualRefundStatus { PendingManualRefund = 1, Completed = 2 }
public enum ManualRefundKind { SettlementDeposit = 1, RentalRefund = 2 }

public sealed class ManualRefundRequirement
{
    public Guid Id { get; private set; }
    public string RequirementRef { get; private set; } = string.Empty;
    public Guid? SettlementId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid? RentalItemId { get; private set; }
    public Guid RenterId { get; private set; }
    public string PaymentRef { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string OperationKey { get; private set; } = string.Empty;
    public ManualRefundKind Kind { get; private set; }
    public decimal Amount { get; private set; }
    public ManualRefundStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public string? ExternalTransferReference { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public RentalItemSettlementRecord? Settlement { get; private set; }
    public List<ManualRefundItem> Items { get; private set; } = [];

    private ManualRefundRequirement() { }

    public static ManualRefundRequirement Create(Guid invoiceId, Guid renterId, string paymentRef,
        string operationKey, string reason, ManualRefundKind kind, Guid? settlementId,
        IReadOnlyList<ManualRefundItem> items, DateTimeOffset now)
    {
        Guid id = Guid.NewGuid();
        foreach (var item in items) item.AssignRequirement(id);
        return new ManualRefundRequirement
        {
            Id = id, RequirementRef = $"MRF-{id:N}", InvoiceId = invoiceId, RenterId = renterId,
            PaymentRef = paymentRef, OperationKey = operationKey, Reason = reason, Kind = kind,
            SettlementId = settlementId, RentalItemId = items.Count == 1 ? items[0].RentalItemId : null,
            Amount = items.Sum(x => x.RentalAmount + x.DepositAmount), Items = items.ToList(),
            Status = ManualRefundStatus.PendingManualRefund, CreatedAt = now.ToUniversalTime()
        };
    }

    public void AssignCompleted(string reference, Guid adminId, DateTimeOffset now)
    {
        Status = ManualRefundStatus.Completed;
        ExternalTransferReference = reference;
        CompletedBy = adminId;
        CompletedAt = now.ToUniversalTime();
        Revision++;
    }
}

public sealed class ManualRefundItem
{
    public Guid RequirementId { get; private set; }
    public Guid RentalItemId { get; private set; }
    public decimal RentalAmount { get; private set; }
    public decimal DepositAmount { get; private set; }
    private ManualRefundItem() { }
    public static ManualRefundItem Create(Guid itemId, decimal rentalAmount, decimal depositAmount) =>
        new() { RentalItemId = itemId, RentalAmount = rentalAmount, DepositAmount = depositAmount };
    internal void AssignRequirement(Guid id) => RequirementId = id;
}
