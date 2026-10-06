using Dprental.InventoryService.Domain.Kafka.Contracts;
using Dprental.InventoryService.Domain.Kafka.Transport;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.Kafka.Outbox;

/// <summary>
/// Quản lý tạo các thông điệp outbox cho sự kiện thay đổi tính khả dụng của tài sản (InventoryAssetAvailabilityChanged).
/// Dùng chung giữa CalendarService và nghiệp vụ bật/tắt nhận đặt lịch (accepting-bookings).
/// </summary>
public static class InventoryAssetAvailabilityEvents
{
    public const string MessageType = "InventoryAssetAvailabilityChanged";

    /// <summary>
    /// Tạo OutboxMessage cho sự kiện InventoryAssetAvailabilityChanged chuẩn hóa với AggregateId làm Kafka key.
    /// </summary>
    public static OutboxMessage CreateAvailabilityChangedOutboxMessage(Guid assetId, DateTimeOffset nowUtc)
    {
        var payload = new InventoryAssetAvailabilityChangedPayload(assetId, nowUtc);
        var envelope = MessageEnvelope.CreateRoot(
            messageType: MessageType,
            aggregateId: assetId.ToString("D"),
            payload: payload,
            version: 1,
            occurredAt: nowUtc);

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryAssetEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: wireJson,
            now: nowUtc);
    }
}
