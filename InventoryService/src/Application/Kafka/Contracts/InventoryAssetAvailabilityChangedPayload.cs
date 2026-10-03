using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Payload sự kiện thay đổi lịch khả dụng phát ra Kafka qua Outbox (v1).
/// </summary>
public sealed record InventoryAssetAvailabilityChangedPayload(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("changedAt")] DateTimeOffset ChangedAt
);
