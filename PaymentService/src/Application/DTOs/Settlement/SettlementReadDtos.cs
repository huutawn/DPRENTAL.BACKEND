using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.DTOs.Settlement
{
    public sealed record SettlementRefundView(
    decimal DepositRefundDue, string? RequirementRef, string? RefundStatus,
    string? ExternalTransferReference, DateTimeOffset? RefundCompletedAt);

    public sealed record SettlementSupplierView(
        decimal SupplierPayable, decimal PlatformFeeRate, decimal PlatformFeeAmount,
        string? PayoutRef, string? PayoutStatus, DateTimeOffset? PayoutCompletedAt);

    public sealed record SettlementDetailResponse(
        Guid Id, string SettlementRef, Guid RentalItemId, string Status,
        decimal RentalAmount, decimal DepositAmount, decimal FinalDeductionAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
        string? DecisionRef,                       // chỉ admin
        SettlementRefundView? Refund,              // renter, admin
        SettlementSupplierView? Supplier);         // supplier, admin

    public sealed record ManualRefundListItem(
        Guid RequirementId, string RequirementRef, string SettlementRef, Guid RentalItemId, Guid RenterId,
        decimal Amount, string Status, string? ExternalTransferReference,
        Guid? CompletedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
}
