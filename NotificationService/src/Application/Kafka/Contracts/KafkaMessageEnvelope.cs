using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.NotificationService.Application.Kafka.Contracts;

public sealed class KafkaMessageEnvelope
{
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Producer { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public JsonElement Payload { get; set; }

    // Rental uses messageId/messageType in rental.events.v1; accept both wire envelopes.
    [JsonPropertyName("messageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MessageId
    {
        get => null;
        set { if (value.HasValue) EventId = value.Value.ToString("D"); }
    }

    [JsonPropertyName("messageType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageType
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value)) EventType = value; }
    }
}
