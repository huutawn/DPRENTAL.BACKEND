using System.Text.Json;

namespace Dprental.NotificationService.Application.Kafka.Contracts;

public sealed class KafkaMessageEnvelope
{
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Producer { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public JsonElement Payload { get; set; }
}
