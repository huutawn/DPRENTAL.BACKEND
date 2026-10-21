using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.Extensions.Options;
using Dprental.RentalService.Application.Kafka.Configuration;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaClientProtocolAndSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void RentalEventEnvelope_SerializesWithCanonicalFields()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var payload = new RentalItemConfirmedPayload(
            orderId,
            itemId,
            Guid.NewGuid(),
            now,
            "REC-CONF-001");

        var envelope = new KafkaMessageEnvelope<RentalItemConfirmedPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.RentalItemConfirmed,
            1,
            "rental-service",
            orderId.ToString("D"),
            now,
            "corr-123",
            null,
            payload);

        string json = JsonSerializer.Serialize(envelope, JsonOptions);

        Assert.Contains("\"messageType\":\"RentalItemConfirmed\"", json);
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"producer\":\"rental-service\"", json);
        Assert.Contains($"\"aggregateId\":\"{orderId}\"", json);
        Assert.Contains("\"inventoryConfirmationRef\":\"REC-CONF-001\"", json);

        var deserialized = JsonSerializer.Deserialize<KafkaMessageEnvelope<RentalItemConfirmedPayload>>(json, JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(envelope.MessageId, deserialized.MessageId);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal("REC-CONF-001", deserialized.Payload.InventoryConfirmationRef);
    }

    [Fact]
    public void DeadLetterPayload_SerializesCorrectlyForDlq()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var dlqPayload = new DeadLetterPayload("PaymentEvent", "{bad json}", "MALFORMED_JSON: invalid syntax", now);

        var dlqEnvelope = new KafkaMessageEnvelope<DeadLetterPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.DeadLetter,
            1,
            "rental-service",
            "system",
            now,
            null,
            null,
            dlqPayload);

        string json = JsonSerializer.Serialize(dlqEnvelope, JsonOptions);
        Assert.Contains("\"messageType\":\"DeadLetter\"", json);
        Assert.Contains("MALFORMED_JSON", json);
    }

    [Fact]
    public void OutboxMessage_PartitionKey_AlwaysMatchesRentalOrderId()
    {
        Guid orderId = Guid.NewGuid();
        var msg = new RentalOutboxMessage(
            Guid.NewGuid(),
            "rental.events.v1",
            orderId.ToString("D"),
            KafkaContractSpec.EventTypes.RentalItemPaid,
            "{}",
            DateTimeOffset.UtcNow);

        Assert.Equal(orderId.ToString("D"), msg.PartitionKey);
        Assert.Equal("rental.events.v1", msg.Topic);
        Assert.Equal("Pending", msg.Status);
    }

    [Fact]
    public void KafkaPaymentEventsConsumerWorker_InitializesWithoutException_WhenBootstrapServersConfigured()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new KafkaOptions
        {
            Enabled = false,
            BootstrapServers = "127.0.0.1:9092",
            PaymentEventsTopic = "payment.events.v1",
            ConsumerGroupId = "rental-consumer-test"
        });

        var worker = new KafkaPaymentEventsConsumerWorker(scopeFactory, options, NullLogger<KafkaPaymentEventsConsumerWorker>.Instance);
        Assert.NotNull(worker);
        worker.Dispose();
    }
}
