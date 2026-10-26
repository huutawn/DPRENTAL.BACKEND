using System.Text.Json.Serialization;

namespace Dprental.AnalyticsService.Application.Events;

/// <summary>Envelope chung của event được publish lên Kafka cho Analytics.</summary>
public sealed record AnalyticsEventEnvelope<TPayload>(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("aggregateId")] string AggregateId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("causationId")] string? CausationId,
    [property: JsonPropertyName("payload")] TPayload Payload);

public static class AnalyticsEventTopics
{
    public const string RentalEventsV1 = "rental.events.v1";
    public const string PaymentAnalyticsV1 = "payment.analytics.v1";
}

public static class PaymentAnalyticsEventTypes
{
    public const string InvoicePaidFinancial = "InvoicePaidFinancial";
    public const string RentalItemFinancialSettled = "RentalItemFinancialSettled";
    public const string RefundCompletedFinancial = "RefundCompletedFinancial";
    public const string SupplierPayoutCompleted = "SupplierPayoutCompleted";
}
