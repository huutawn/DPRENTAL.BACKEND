using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.DTOs.Settlement
{
    public sealed record SupplierEarningsBreakdownResponse(
        decimal RentalAmount,
        decimal PlatformFeeRate,
        decimal PlatformFeeAmount,
        decimal FinalDeductionAmount,
        decimal DeliveryFeeEarned,
        decimal ReturnFeeEarned,
        decimal ShippingFeeEarned,
        decimal NetSupplierPayable);

    public sealed record SettlementRefundView(
        decimal DepositRefundDue, string? RequirementRef, string? RefundStatus,
        string? ExternalTransferReference, DateTimeOffset? RefundCompletedAt);

    public sealed record SettlementSupplierView(
        decimal SupplierPayable, decimal PlatformFeeRate, decimal PlatformFeeAmount,
        string? PayoutRef, string? PayoutStatus, DateTimeOffset? PayoutCompletedAt,
        decimal DeliveryFeeEarned = 0m, decimal ReturnFeeEarned = 0m, decimal ShippingFeeEarned = 0m,
        decimal NetSupplierPayable = 0m,
        SupplierEarningsBreakdownResponse? EarningsBreakdown = null);

    public sealed record SettlementDetailResponse(
        Guid Id, string SettlementRef, Guid RentalItemId, string Status,
        decimal RentalAmount, decimal DepositAmount, decimal FinalDeductionAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
        string? DecisionRef,                       // chỉ admin
        SettlementRefundView? Refund,              // renter, admin
        SettlementSupplierView? Supplier,          // supplier, admin
        decimal DeliveryFeeEarned = 0m,
        decimal ReturnFeeEarned = 0m,
        SupplierEarningsBreakdownResponse? EarningsBreakdown = null);

    public sealed record ManualRefundListItem(
        Guid RequirementId, string RequirementRef, string? SettlementRef, Guid? RentalItemId, Guid RenterId,
        decimal Amount, string Status, string TransferCode, string? ExternalTransferReference,
        Guid? CompletedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
        string? RecipientBankCode, string? RecipientAccountNumber, string? RecipientAccountHolderName, string? QrUrl);
}
