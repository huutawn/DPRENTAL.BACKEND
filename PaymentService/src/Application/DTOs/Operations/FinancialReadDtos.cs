using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.DTOs.Operations
{
    public sealed record TransactionDetailResponse(
    Guid Id, string Provider, string ProviderTransactionId, string PaymentRef, string? InvoiceRef,
    decimal Amount, string Status, string ReasonCode, DateTimeOffset TransactionDate,
    DateTimeOffset CreatedAt, string? ResolvedInvoiceRef, DateTimeOffset? ResolvedAt);

    public sealed record FinancialReviewItem(
        string Kind, Guid Id, string Reference, string Status, string? ReasonCode,
        decimal? Amount, string? RelatedRef,
        DateTimeOffset CreatedAt);
}
