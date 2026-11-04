using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.NotificationService.Application.Kafka.Contracts;

/// <summary>
/// Vỏ bản tin Kafka phiên bản 1 chuẩn hóa dùng chung toàn hệ sinh thái DPRENTAL (Rental, Payment, Review, Notification).
/// </summary>
public sealed class KafkaMessageEnvelope
{
    [JsonPropertyName("messageId")]
    public Guid MessageId { get; set; }

    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = string.Empty;

    [JsonPropertyName("aggregateId")]
    public string AggregateId { get; set; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("causationId")]
    public Guid? CausationId { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonPropertyName("organizationId")]
    public Guid? OrganizationId { get; set; }
}
