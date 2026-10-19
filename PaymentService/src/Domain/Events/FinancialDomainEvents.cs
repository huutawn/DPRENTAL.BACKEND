namespace Dprental.PaymentService.Domain.Events;

public interface IFinancialDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public sealed record InvoiceCreatedDomainEvent(
    Guid InvoiceId,
    string InvoiceRef,
    Guid RentalOrderId,
    string OrderCode,
    Guid RenterId,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record InvoicePaidDomainEvent(
    Guid InvoiceId,
    string InvoiceRef,
    Guid RentalOrderId,
    string PaymentRef,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PaidAt,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record InvoiceCancelledDomainEvent(
    Guid InvoiceId,
    string InvoiceRef,
    Guid RentalOrderId,
    string? CancelReason,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record InvoiceExpiredDomainEvent(
    Guid InvoiceId,
    string InvoiceRef,
    Guid RentalOrderId,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record BankTransactionReceivedDomainEvent(
    Guid TransactionId,
    string Provider,
    string ProviderTransactionId,
    string PaymentRef,
    string TransferType,
    decimal TransferAmount,
    string Gateway,
    string AccountNumber,
    string? PaymentCode,
    string? ReferenceCode,
    string Status,
    string ReasonCode,
    Guid? InvoiceId,
    string? InvoiceRef,
    DateTimeOffset BankTimeUtc,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record RentalItemSettledDomainEvent(
    Guid SettlementId,
    string SettlementRef,
    string OperationKey,
    Guid InvoiceId,
    Guid InvoiceItemId,
    Guid RentalItemId,
    Guid SupplierId,
    decimal RentalAmount,
    decimal DepositAmount,
    decimal FinalDeductionAmount,
    decimal DepositRefundDue,
    decimal PlatformFeeAmount,
    decimal SupplierPayable,
    int Status,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record ManualRefundRequiredDomainEvent(
    Guid RequirementId,
    string RequirementRef,
    Guid SettlementId,
    Guid RentalItemId,
    decimal Amount,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;

public sealed record ManualRefundConfirmedDomainEvent(
    Guid RequirementId,
    string RequirementRef,
    Guid SettlementId,
    Guid RentalItemId,
    decimal Amount,
    string ExternalTransferReference,
    Guid AdminId,
    DateTimeOffset OccurredAt) : IFinancialDomainEvent;
