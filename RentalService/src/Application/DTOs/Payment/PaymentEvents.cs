using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common.Utils;

namespace Dprental.RentalService.Application.DTOs.Payment;

/// <summary>
/// Canonical standard integration event envelope for Payment domain events.
/// Schema Version: 1, Partition Key: AggregateId (RentalOrderId).
/// </summary>
public sealed record PaymentEventEnvelope<TPayload>(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("aggregateId")] string AggregateId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("causationId")] Guid? CausationId,
    [property: JsonPropertyName("payload")] TPayload Payload
);

/// <summary>
/// Event payload for InvoicePaid event.
/// </summary>
public sealed record InvoicePaidPayload(
    [Required] Guid RentalOrderId,
    [Required] Guid RenterId,
    [Required] string InvoiceRef,
    [Required] string PaymentRef,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal PaidAmount,
    [Required] string Currency,
    [Required] DateTimeOffset PaidAt);

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
/// Event payload for RefundCompleted event.
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
/// Event payload for RentalSettled event.
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
