using System.Text.Json;

namespace Dprental.AnalyticsService.Application.Kafka.Contracts;

/// <summary>
/// Phong bì chung của mọi message Kafka (Envelope v1). Payload được giữ nguyên dạng JSON
/// cho đến khi biết messageType, rồi mới đọc bằng DTO tương ứng.
/// </summary>
public sealed record KafkaEnvelope
{
    public required Guid MessageId { get; init; }      // Dùng để chống xử lý trùng (inbox)
    public required string MessageType { get; init; }  // Quyết định đọc payload bằng DTO nào
    public required DateTimeOffset OccurredAt { get; init; }
    public string? Producer { get; init; }
    public required JsonElement Payload { get; init; }
}
