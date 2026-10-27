using System.Text.Json;

namespace Dprental.AnalyticsService.Application.Events;

/// <summary>
/// Phong bì chung (Envelope v1) của mọi message. Payload giữ nguyên dạng JSON cho đến khi biết
/// messageType, rồi mới đọc bằng DTO đúng loại.
/// </summary>
public sealed record KafkaEnvelope
{
    public const int SupportedVersion = 1;

    public required Guid MessageId { get; init; }      // Chống xử lý trùng
    public required string MessageType { get; init; }  // Quyết định đọc payload bằng DTO nào
    public required int Version { get; init; }
    public string? AggregateId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required JsonElement Payload { get; init; }
}
