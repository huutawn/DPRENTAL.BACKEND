namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Whitelisted financial facts received from Payment. This stores no bank or account details.
/// A row represents one immutable business event; MessageId and LogicalEventId make delivery
/// and business replays idempotent.
/// </summary>
public sealed class PaymentFinancialEvent
{
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string LogicalEventId { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }

    public Guid? InvoiceId { get; set; }
    public string? InvoiceRef { get; set; }
    public Guid? RentalOrderId { get; set; }
    public Guid? RenterId { get; set; }
    public decimal? RentalAmount { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public Guid? SettlementId { get; set; }
    public string? SettlementRef { get; set; }
    public Guid? RentalItemId { get; set; }
    public Guid? SupplierId { get; set; }
    public decimal? FinalDeductionAmount { get; set; }
    public decimal? DepositRefundDue { get; set; }
    public decimal? PlatformFeeRate { get; set; }
    public decimal? PlatformFeeAmount { get; set; }
    public decimal? SupplierPayable { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? RefundId { get; set; }
    public string? RefundRef { get; set; }
    public string? RefundKind { get; set; }
    public decimal? Amount { get; set; }

    public Guid? PayoutId { get; set; }
    public string? PayoutRef { get; set; }
    public string Currency { get; set; } = "VND";
}
