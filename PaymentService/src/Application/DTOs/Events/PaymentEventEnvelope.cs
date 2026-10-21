using System.Text.Json.Serialization;

namespace Dprental.PaymentService.Application.DTOs.Events;

public sealed record PaymentEventEnvelope<TPayload>(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("aggregateId")] string AggregateId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("causationId")] string? CausationId,
    [property: JsonPropertyName("payload")] TPayload Payload);

public sealed record InvoicePaidPayload(
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("renterId")] Guid RenterId,
    [property: JsonPropertyName("invoiceRef")] string InvoiceRef,
    [property: JsonPropertyName("paymentRef")] string PaymentRef,
    [property: JsonPropertyName("paidAmount")] decimal PaidAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("paidAt")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("paymentDisposition")] string PaymentDisposition,
    [property: JsonPropertyName("processedAt")] DateTimeOffset ProcessedAt);

public static class InvoicePaidDispositions
{
    public const string OnTime = "ON_TIME";
    public const string LateRequiresRefund = "LATE_REQUIRES_REFUND";
}
