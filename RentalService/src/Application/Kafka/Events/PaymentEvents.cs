using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common.Utils;

namespace Dprental.RentalService.Application.Kafka.Events;

/// <summary>
/// Event payload for InvoicePaid event from payment-service.
/// </summary>
public sealed record InvoicePaidPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RenterId,
    [Required] string InvoiceRef,
    [Required] string PaymentRef,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal PaidAmount,
    [Required] string Currency,
    [Required] DateTimeOffset PaidAt,
    string PaymentDisposition = "ON_TIME",
    DateTimeOffset? ProcessedAt = null);

/// <summary>
/// Breakdown item for RefundCompleted event.
/// </summary>
public sealed record RefundCompletedItemPayload(
    [Required] Guid RentalItemId,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedRentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedDepositAmount)
{
    [JsonIgnore]
    public decimal TotalRefundedAmount => RefundedRentalAmount + RefundedDepositAmount;
}

/// <summary>
/// Event payload for RefundCompleted event from payment-service.
/// </summary>
public sealed record RefundCompletedPayload(
    [Required] Guid RentalOrderId,
    [Required] string InvoiceRef,
    [Required] string PaymentRef,
    [Required] string RefundRef,
    [Required] string OperationKey,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal TotalRefundedAmount,
    [Required] string Currency,
    [Required] IReadOnlyList<RefundCompletedItemPayload> Items,
    [Required] DateTimeOffset CompletedAt);

/// <summary>
/// Event payload for RentalSettled event from payment-service.
/// </summary>
public sealed record RentalSettledPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required] string InvoiceRef,
    [Required] string PaymentRef,
    [Required] string SettlementRef,
    [Required] string OperationKey,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal FinalDeductionAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedDepositAmount,
    [Required] string Currency,
    string? RefundRef,
    [Required] DateTimeOffset CompletedAt);
