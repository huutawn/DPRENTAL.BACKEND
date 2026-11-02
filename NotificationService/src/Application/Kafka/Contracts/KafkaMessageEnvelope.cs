using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.NotificationService.Application.Kafka.Contracts;

public sealed class KafkaMessageEnvelope
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}
