
namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Payload sự kiện thay đổi lịch khả dụng phát ra Kafka qua Outbox (v1).
/// </summary>
public sealed record InventoryAssetAvailabilityChangedPayload(
    Guid AssetId,
    DateTimeOffset ChangedAt
);
