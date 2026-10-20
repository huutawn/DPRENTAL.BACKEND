using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.Kafka.Contracts;

/// <summary>
/// Unified Kafka Message Envelope (Version 1).
/// Canonical standard integration contract across Rental, Payment, and Inventory streaming.
/// </summary>
public sealed record KafkaMessageEnvelope<TPayload>(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("aggregateId")] string AggregateId,
    [property: JsonPropertyName("occurredAt"), JsonConverter(typeof(UtcIsoDateTimeOffsetConverter))] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("causationId")] Guid? CausationId,
    [property: JsonPropertyName("payload")] TPayload Payload,
    [property: JsonPropertyName("organizationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OrganizationId = null);
