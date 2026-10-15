using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Kafka.Outbox;

public static class InventoryAllocationOutboxEvents
{
    public static OutboxMessage CreateSucceededOutboxMessage(
        Guid rentalOrderId,
        InventoryAllocationCommandSucceededPayload payload,
        string? correlationId,
        Guid? causationId,
        DateTimeOffset nowUtc)
    {
        var envelope = new MessageEnvelope<InventoryAllocationCommandSucceededPayload>
        {
            MessageId = Guid.NewGuid(),
            MessageType = AllocationMessageTypes.Succeeded,
            Version = 1,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? rentalOrderId.ToString("D") : correlationId,
            CausationId = causationId,
            AggregateId = rentalOrderId.ToString("D"),
            OrganizationId = null,
            OccurredAt = BusinessDateTime.NormalizeUtc(nowUtc),
            Producer = MessageEnvelope.DefaultProducer,
            Payload = payload
        };

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryAllocationEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: wireJson,
            now: nowUtc);
    }

    public static OutboxMessage CreateRejectedOutboxMessage(
        Guid rentalOrderId,
        InventoryAllocationCommandRejectedPayload payload,
        string? correlationId,
        Guid? causationId,
        DateTimeOffset nowUtc)
    {
        var envelope = new MessageEnvelope<InventoryAllocationCommandRejectedPayload>
        {
            MessageId = Guid.NewGuid(),
            MessageType = AllocationMessageTypes.Rejected,
            Version = 1,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? rentalOrderId.ToString("D") : correlationId,
            CausationId = causationId,
            AggregateId = rentalOrderId.ToString("D"),
            OrganizationId = null,
            OccurredAt = BusinessDateTime.NormalizeUtc(nowUtc),
            Producer = MessageEnvelope.DefaultProducer,
            Payload = payload
        };

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryAllocationEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: wireJson,
            now: nowUtc);
    }

    public static OutboxMessage CreateReturnedUnitsAppliedOutboxMessage(
        Guid rentalOrderId,
        ReturnedUnitsAppliedPayload payload,
        string? correlationId,
        Guid? causationId,
        DateTimeOffset nowUtc)
    {
        var envelope = new MessageEnvelope<ReturnedUnitsAppliedPayload>
        {
            MessageId = Guid.NewGuid(),
            MessageType = AllocationMessageTypes.ReturnedUnitsApplied,
            Version = 1,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? rentalOrderId.ToString("D") : correlationId,
            CausationId = causationId,
            AggregateId = rentalOrderId.ToString("D"),
            OrganizationId = null,
            OccurredAt = BusinessDateTime.NormalizeUtc(nowUtc),
            Producer = MessageEnvelope.DefaultProducer,
            Payload = payload
        };

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryAllocationEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: wireJson,
            now: nowUtc);
    }

    public static OutboxMessage CreateReturnedUnitsRejectedOutboxMessage(
        Guid rentalOrderId,
        ReturnedUnitsRejectedPayload payload,
        string? correlationId,
        Guid? causationId,
        DateTimeOffset nowUtc)
    {
        var envelope = new MessageEnvelope<ReturnedUnitsRejectedPayload>
        {
            MessageId = Guid.NewGuid(),
            MessageType = AllocationMessageTypes.ReturnedUnitsRejected,
            Version = 1,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? rentalOrderId.ToString("D") : correlationId,
            CausationId = causationId,
            AggregateId = rentalOrderId.ToString("D"),
            OrganizationId = null,
            OccurredAt = BusinessDateTime.NormalizeUtc(nowUtc),
            Producer = MessageEnvelope.DefaultProducer,
            Payload = payload
        };

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);

        return OutboxMessage.Create(
            id: envelope.MessageId,
            topic: KafkaTopics.InventoryAllocationEventsV1,
            kafkaKey: envelope.KafkaKey,
            payload: wireJson,
            now: nowUtc);
    }
}
