using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

public enum FinancialReviewKind
{
    AwaitingPayment,
    UnmatchedTransaction,
    LateTransaction,
    ReviewRequiredTransaction,
    SettlementPendingClose
}

public sealed record PageRequest(int Page, int PageSize)
{
    public int Skip => (Page - 1) * PageSize;
}

public sealed record DateRange(DateTimeOffset? From, DateTimeOffset? To);
public sealed record PayoutFilter(string? Status, DateTimeOffset? From, DateTimeOffset? To);
public sealed record ManualRefundFilter(ManualRefundStatus? Status, DateTimeOffset? From, DateTimeOffset? To);
public sealed record ReadPage<T>(IReadOnlyList<T> Items, long TotalCount);

public sealed record InvoiceStatusItemRow(
    Guid RentalItemId, decimal RentalAmount, decimal DepositAmount, decimal TotalAmount,
    SettlementState? SettlementStatus);

public sealed record InvoiceStatusRow(
    string InvoiceRef, Guid RentalOrderId, string Status, DateTimeOffset PaymentDeadline,
    DateTimeOffset? PaidAt, IReadOnlyList<InvoiceStatusItemRow> Items);

public sealed record TransactionDetailRow(
    Guid Id, string Provider, string ProviderTransactionId, string PaymentRef, string? InvoiceRef,
    decimal Amount, string Status, string ReasonCode, DateTimeOffset TransactionDate,
    DateTimeOffset CreatedAt, string? ResolvedInvoiceRef, DateTimeOffset? ResolvedAt);

public sealed record SettlementDetailRow(
    Guid Id, string SettlementRef, Guid RentalItemId, SettlementState Status,
    decimal RentalAmount, decimal DepositAmount, decimal FinalDeductionAmount, decimal DepositRefundDue,
    decimal SupplierPayable, decimal PlatformFeeRate, decimal PlatformFeeAmount, string DecisionRef,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    string? RefundRef, ManualRefundStatus? RefundStatus, string? RefundTransferReference, DateTimeOffset? RefundCompletedAt,
    string? PayoutRef, string? PayoutStatus, DateTimeOffset? PayoutCompletedAt,
    bool ViewerIsRenter, bool ViewerIsSupplier);

public sealed record SupplierPayoutRow(
    Guid PayoutId, string PayoutRef, Guid RentalItemId, string SettlementRef,
    decimal Amount, string Currency, string Status, string? ExternalTransferReference,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record AdminPayoutRow(
    Guid PayoutId, string PayoutRef, Guid SettlementId, string SettlementRef, Guid RentalItemId, Guid SupplierId,
    decimal Amount, string Currency, string Status, string? DestinationRef, string? ExternalTransferReference,
    Guid? CompletedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    Guid? DestinationId, int? DestinationVersion);

public sealed record ManualRefundRow(
    Guid RequirementId, string RequirementRef, string SettlementRef, Guid RentalItemId, Guid RenterId,
    decimal Amount, ManualRefundStatus Status, string? ExternalTransferReference,
    Guid? CompletedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record ReviewRow(
    Guid Id, string Reference, string Status, string? ReasonCode, decimal? Amount,
    string? RelatedRef, DateTimeOffset CreatedAt);

public interface IFinancialReadRepository
{
    Task<InvoiceStatusRow?> GetInvoiceStatusAsync(string invoiceRef, Guid renterId, CancellationToken ct);
    Task<TransactionDetailRow?> GetTransactionAsync(Guid id, CancellationToken ct);
    Task<SettlementDetailRow?> GetSettlementAsync(Guid id, Guid callerId, bool isAdmin, CancellationToken ct);
    Task<ReadPage<SupplierPayoutRow>> ListSupplierPayoutsAsync(Guid supplierId, PayoutFilter f, PageRequest p, CancellationToken ct);
    Task<ReadPage<AdminPayoutRow>> ListAdminPayoutsAsync(Guid? supplierId, PayoutFilter f, PageRequest p, CancellationToken ct);
    Task<ReadPage<ManualRefundRow>> ListManualRefundsAsync(ManualRefundFilter f, PageRequest p, CancellationToken ct);
    Task<ReadPage<ReviewRow>> ListReviewAsync(FinancialReviewKind kind, DateRange r, PageRequest p, CancellationToken ct);
}
