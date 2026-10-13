using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Payload sự kiện thay đổi dữ liệu cần cập nhật chỉ mục tìm kiếm (v1).
/// </summary>
public sealed record SearchProjectionChangedPayload(
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] Guid EntityId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("occurredAtUtc")] DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("needsAssetFanout")] bool NeedsAssetFanout = false,
    [property: JsonPropertyName("runId")] Guid? RunId = null
);
