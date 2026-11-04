using System.Text.Json.Serialization;

namespace Dprental.ReviewService.Application.Kafka.Contracts;

/// <summary>
/// Vỏ bản tin Kafka envelope v1 dùng chung giữa các microservice (RentalService, ReviewService, etc.).
/// </summary>
public sealed record KafkaMessageEnvelope<TPayload>(
    Guid MessageId,
    string MessageType,
    int Version,
    string Producer,
    string AggregateId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    Guid? CausationId,
    TPayload Payload,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OrganizationId = null);
