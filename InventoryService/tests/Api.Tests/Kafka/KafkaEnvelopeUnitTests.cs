using System.Text.Json;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Kafka;

public sealed class KafkaEnvelopeUnitTests
{
    private sealed record TestPayload(string ItemName, int Quantity);

    [Fact]
    public void MessageEnvelope_ValidEnvelope_RoundtripsThroughJson()
    {
        var orgId = Guid.NewGuid();
        var aggregateId = "EQ-100";
        var payload = new TestPayload("Camera Sony A7IV", 2);

        var root = MessageEnvelope.CreateRoot(
            messageType: "equipment.hold.created",
            aggregateId: aggregateId,
            payload: payload,
            version: 1,
            organizationId: orgId);

        var json = KafkaEnvelopeSerializer.Serialize(root);

        // Verify JSON camelCase
        Assert.Contains("\"messageId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"messageType\":", json, StringComparison.Ordinal);
        Assert.Contains("\"organizationId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"aggregateId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"occurredAt\":", json, StringComparison.Ordinal);
        Assert.Contains("\"producer\":", json, StringComparison.Ordinal);
        Assert.Contains("\"payload\":", json, StringComparison.Ordinal);

        var deserialized = KafkaEnvelopeSerializer.Deserialize<TestPayload>(json);

        Assert.Equal(root.MessageId, deserialized.MessageId);
        Assert.Equal(root.MessageType, deserialized.MessageType);
        Assert.Equal(root.Version, deserialized.Version);
        Assert.Equal(root.CorrelationId, deserialized.CorrelationId);
        Assert.Null(deserialized.CausationId);
        Assert.Equal(root.AggregateId, deserialized.AggregateId);
        Assert.Equal(root.OrganizationId, deserialized.OrganizationId);
        Assert.Equal(root.OccurredAt, deserialized.OccurredAt);
        Assert.Equal(root.Producer, deserialized.Producer);
        Assert.Equal(root.Payload.ItemName, deserialized.Payload.ItemName);
        Assert.Equal(root.Payload.Quantity, deserialized.Payload.Quantity);
        Assert.Equal(aggregateId, deserialized.KafkaKey);
    }

    [Fact]
    public void MessageEnvelope_WithoutOrganizationId_SerializesAndValidatesSuccessfully()
    {
        var aggregateId = "ASSET-999";
        var payload = new TestPayload("Sony FX3", 1);

        var root = MessageEnvelope.CreateRoot(
            messageType: "InventoryAssetAvailabilityChanged",
            aggregateId: aggregateId,
            payload: payload);

        var json = KafkaEnvelopeSerializer.Serialize(root);

        Assert.DoesNotContain("\"organizationId\"", json, StringComparison.Ordinal);
        Assert.Equal(aggregateId, root.KafkaKey);

        var deserialized = KafkaEnvelopeSerializer.Deserialize<TestPayload>(json);
        Assert.Null(deserialized.OrganizationId);
        Assert.Equal(aggregateId, deserialized.KafkaKey);

        var metadata = KafkaEnvelopeSerializer.ExtractMetadata(json);
        Assert.True(metadata.IsValid);
        Assert.Null(metadata.OrganizationId);
    }

    [Fact]
    public void MessageEnvelope_LegacyJsonWithOrganizationId_CanBeDeserialized()
    {
        var orgId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        var aggregateId = "ASSET-OLD-1";
        var legacyJson = $$"""
        {
            "messageId": "{{msgId}}",
            "messageType": "InventoryAssetAvailabilityChanged",
            "version": 1,
            "correlationId": "{{msgId}}",
            "causationId": null,
            "aggregateId": "{{aggregateId}}",
            "organizationId": "{{orgId}}",
            "occurredAt": "2026-10-02T10:00:00Z",
            "producer": "InventoryService",
            "payload": {
                "itemName": "Legacy FX3",
                "quantity": 1
            }
        }
        """;

        var deserialized = KafkaEnvelopeSerializer.Deserialize<TestPayload>(legacyJson);
        Assert.Equal(orgId, deserialized.OrganizationId);
        Assert.Equal(aggregateId, deserialized.KafkaKey);

        var metadata = KafkaEnvelopeSerializer.ExtractMetadata(legacyJson);
        Assert.True(metadata.IsValid);
        Assert.Equal(orgId, metadata.OrganizationId);
    }

    [Theory]
    [InlineData("", "ValidType", 1, "corr", "agg", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "", 1, "corr", "agg", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "ValidType", 0, "corr", "agg", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "ValidType", -1, "corr", "agg", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "ValidType", 1, "", "agg", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "ValidType", 1, "corr", "", "ValidProd")]
    [InlineData("00000000-0000-0000-0000-000000000000", "ValidType", 1, "corr", "agg", "")]
    public void MessageEnvelope_Validation_RejectsInvalidFields(
        string messageIdStr,
        string messageType,
        int version,
        string correlationId,
        string aggregateId,
        string producer)
    {
        var msgId = Guid.TryParse(messageIdStr, out var g) ? g : Guid.Empty;
        var envelope = new MessageEnvelope<string>
        {
            MessageId = msgId,
            MessageType = messageType,
            Version = version,
            CorrelationId = correlationId,
            AggregateId = aggregateId,
            OrganizationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Producer = producer,
            Payload = "test"
        };

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    [Fact]
    public void MessageEnvelope_Validation_RejectsNonUtcTimestamp()
    {
        var envelope = new MessageEnvelope<string>
        {
            MessageId = Guid.NewGuid(),
            MessageType = "test.event",
            Version = 1,
            CorrelationId = "corr-1",
            AggregateId = "agg-1",
            OrganizationId = Guid.NewGuid(),
            OccurredAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(7)), // Non-UTC offset
            Producer = "TestProducer",
            Payload = "test"
        };

        Assert.Throws<ArgumentException>(() => envelope.Validate());
    }

    [Fact]
    public void MessageEnvelope_CorrelationAndCausation_ProperlyChained()
    {
        var orgId = Guid.NewGuid();

        // 1. Root message
        var root = MessageEnvelope.CreateRoot(
            messageType: "reservation.requested",
            aggregateId: "RES-101",
            payload: new TestPayload("Drone DJI Mini 4 Pro", 1),
            organizationId: orgId);

        Assert.NotEqual(Guid.Empty, root.MessageId);
        Assert.Equal(root.MessageId.ToString("D"), root.CorrelationId);
        Assert.Null(root.CausationId);

        // 2. Continuation message follows root
        var continuation = MessageEnvelope.CreateContinuation(
            incoming: root,
            messageType: "reservation.confirmed",
            aggregateId: "RES-101",
            payload: new TestPayload("Drone DJI Mini 4 Pro", 1),
            organizationId: orgId);

        Assert.NotEqual(root.MessageId, continuation.MessageId);
        Assert.Equal(root.CorrelationId, continuation.CorrelationId); // Giữ nguyên CorrelationId
        Assert.Equal(root.MessageId, continuation.CausationId); // CausationId trỏ tới MessageId của root

        // 3. Retry retains exact envelope and MessageId
        var retried = continuation with { };
        Assert.Equal(continuation.MessageId, retried.MessageId);
        Assert.Equal(continuation.CorrelationId, retried.CorrelationId);
        Assert.Equal(continuation.CausationId, retried.CausationId);
    }

    [Fact]
    public void KafkaEnvelopeSerializer_ExtractMetadata_ReadsWithoutKnowingPayload()
    {
        var orgId = Guid.NewGuid();
        var root = MessageEnvelope.CreateRoot(
            messageType: "equipment.registered",
            aggregateId: "EQ-999",
            payload: new { Lens = "Sony 24-70 GM II", Serial = "SN123456" },
            organizationId: orgId);

        var json = KafkaEnvelopeSerializer.Serialize(root);
        var metadata = KafkaEnvelopeSerializer.ExtractMetadata(json);

        Assert.True(metadata.IsValid);
        Assert.Equal(root.MessageId, metadata.MessageId);
        Assert.Equal(root.MessageType, metadata.MessageType);
        Assert.Equal(root.Version, metadata.Version);
        Assert.Equal(root.CorrelationId, metadata.CorrelationId);
        Assert.Equal(root.AggregateId, metadata.AggregateId);
        Assert.Equal(root.OrganizationId, metadata.OrganizationId);
        Assert.Null(metadata.CausationId);
    }

    [Theory]
    [InlineData(1, 0.0, 1.0)]       // 1s base, 0 jitter
    [InlineData(1, 1.0, 1.2)]       // 1s base + 20% jitter
    [InlineData(1, -1.0, 0.8)]      // 1s base - 20% jitter
    [InlineData(2, 0.0, 2.0)]       // 2s base
    [InlineData(3, 0.0, 4.0)]       // 4s base
    [InlineData(4, 0.0, 8.0)]       // 8s base
    [InlineData(5, 0.0, 16.0)]      // 16s base
    [InlineData(6, 0.0, 32.0)]      // 32s base
    [InlineData(7, 0.0, 60.0)]      // capped at 60s base
    [InlineData(8, 0.0, 60.0)]      // capped at 60s base
    [InlineData(7, 1.0, 72.0)]      // 60s + 20% jitter = 72s
    [InlineData(7, -1.0, 48.0)]     // 60s - 20% jitter = 48s
    public void RetryBackoffCalculator_CalculatesExpectedBackoffAndJitter(int attempt, double randomFraction, double expectedSeconds)
    {
        var delay = RetryBackoffCalculator.CalculateDelay(attempt, randomFraction);
        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 2);
    }

    [Fact]
    public void KafkaHandlerRegistry_RegistersAndFindsHandlers()
    {
        var registry = new KafkaHandlerRegistry();
        registry.Register<TestHandler, TestPayload>(
            topic: "inventory.reservation.events.v1",
            messageType: "ReservationConfirmed",
            version: 1,
            handlerName: "ReservationConfirmedHandler",
            groupId: "inventory-reservation-consumers");

        var found = registry.Find("inventory.reservation.events.v1", "ReservationConfirmed", 1);
        Assert.NotNull(found);
        Assert.Equal("ReservationConfirmedHandler", found.HandlerName);
        Assert.Equal("inventory-reservation-consumers", found.GroupId);

        var notFound = registry.Find("inventory.reservation.events.v1", "ReservationConfirmed", 2);
        Assert.Null(notFound);

        var wrongTopic = registry.Find("other.topic", "ReservationConfirmed", 1);
        Assert.Null(wrongTopic);
    }

    [Fact]
    public void KafkaHandlerRegistry_DuplicateRegistration_ThrowsInvalidOperationException()
    {
        var registry = new KafkaHandlerRegistry();
        registry.Register<TestHandler, TestPayload>(
            topic: "inventory.reservation.events.v1",
            messageType: "ReservationConfirmed",
            version: 1,
            handlerName: "HandlerA",
            groupId: "group-1");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<TestHandler, TestPayload>(
                topic: "inventory.reservation.events.v1",
                messageType: "ReservationConfirmed",
                version: 1,
                handlerName: "HandlerB",
                groupId: "group-1"));

        Assert.Contains("Duplicate handler registration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KafkaHandlerRegistry_DifferentGroups_CanRegisterSameEvent()
    {
        var registry = new KafkaHandlerRegistry();
        registry.Register<TestHandler, TestPayload>(
            topic: "inventory.reservation.events.v1",
            messageType: "ReservationConfirmed",
            version: 1,
            handlerName: "HandlerAudit",
            groupId: "group-audit");

        registry.Register<TestHandler, TestPayload>(
            topic: "inventory.reservation.events.v1",
            messageType: "ReservationConfirmed",
            version: 1,
            handlerName: "HandlerBilling",
            groupId: "group-billing");

        var auditHandler = registry.Find("group-audit", "inventory.reservation.events.v1", "ReservationConfirmed", 1);
        var billingHandler = registry.Find("group-billing", "inventory.reservation.events.v1", "ReservationConfirmed", 1);

        Assert.NotNull(auditHandler);
        Assert.NotNull(billingHandler);
        Assert.Equal("HandlerAudit", auditHandler.HandlerName);
        Assert.Equal("HandlerBilling", billingHandler.HandlerName);
        Assert.Equal(2, registry.GetDistinctGroupIds().Count);
    }

    [Fact]
    public void KafkaEnvelopeSerializer_DeserializeAndValidate_RejectsNullPayload()
    {
        var orgId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // JSON với "payload": null
        var rawJson = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "reservation.created",
                "version": 1,
                "correlationId": "corr-1",
                "aggregateId": "RES-1",
                "organizationId": "{{orgId}}",
                "occurredAt": "{{now:O}}",
                "producer": "Test",
                "payload": null
            }
            """;

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            KafkaEnvelopeSerializer.DeserializeAndValidate(rawJson, typeof(TestPayload)));

        Assert.IsType<ArgumentNullException>(ex.InnerException);
    }


    private sealed class TestHandler : IMessageHandler<TestPayload>
    {
        public Task<Dprental.InventoryService.Common.Result> HandleAsync(
            MessageEnvelope<TestPayload> envelope,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Dprental.InventoryService.Common.Result.Success());
        }
    }

    [Fact]
    public void LegacyOutboxMessage_RetainsExactKafkaKeyAndPayload_WithoutMutation()
    {
        var legacyOrgId = Guid.NewGuid();
        var legacyAssetId = Guid.NewGuid();
        var legacyKafkaKey = $"{legacyOrgId:D}:{legacyAssetId:D}";
        var legacyPayload = "{\"legacyField\":123,\"organizationId\":\"" + legacyOrgId.ToString("D") + "\"}";
        var now = DateTimeOffset.UtcNow;

        var outbox = OutboxMessage.Create(
            id: Guid.NewGuid(),
            topic: "legacy.inventory.events",
            kafkaKey: legacyKafkaKey,
            payload: legacyPayload,
            now: now);

        Assert.Equal(legacyKafkaKey, outbox.KafkaKey);
        Assert.Equal(legacyPayload, outbox.Payload);
        Assert.Equal("legacy.inventory.events", outbox.Topic);
    }
}
