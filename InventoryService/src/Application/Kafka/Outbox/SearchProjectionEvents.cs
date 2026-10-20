using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Kafka.Outbox;

/// <summary>
/// Quản lý tạo các thông điệp Outbox cho sự kiện cập nhật chỉ mục tìm kiếm (Search Projection Events).
/// </summary>
public static class SearchProjectionEvents
{
    public const string ListingMessageType = "InventoryListingProjectionChanged";
    public const string AssetMessageType = "InventoryAssetProjectionChanged";

    public static OutboxMessage CreateListingProjectionOutboxMessage(
        Guid listingId,
        string action,
        long revision,
        bool needsAssetFanout,
        DateTimeOffset nowUtc,
        Guid? runId = null)
    {
        var payload = new SearchProjectionChangedPayload(
            EntityType: "EquipmentListing",
            EntityId: listingId,
            Action: action,
            Revision: revision,
            OccurredAtUtc: nowUtc,
            NeedsAssetFanout: needsAssetFanout,
            RunId: runId);

        var envelope = MessageEnvelope.CreateRoot(
            messageType: ListingMessageType,
            aggregateId: listingId.ToString("D"),
            payload: payload,
            version: 1,
            occurredAt: nowUtc);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryProjectionEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: KafkaEnvelopeSerializer.Serialize(envelope),
            now: nowUtc);
    }

    public static OutboxMessage CreateAssetProjectionOutboxMessage(
        Guid assetId,
        string action,
        long revision,
        DateTimeOffset nowUtc,
        Guid? runId = null)
    {
        var payload = new SearchProjectionChangedPayload(
            EntityType: "InventoryAsset",
            EntityId: assetId,
            Action: action,
            Revision: revision,
            OccurredAtUtc: nowUtc,
            NeedsAssetFanout: false,
            RunId: runId);

        var envelope = MessageEnvelope.CreateRoot(
            messageType: AssetMessageType,
            aggregateId: assetId.ToString("D"),
            payload: payload,
            version: 1,
            occurredAt: nowUtc);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryProjectionEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: KafkaEnvelopeSerializer.Serialize(envelope),
            now: nowUtc);
    }
}
