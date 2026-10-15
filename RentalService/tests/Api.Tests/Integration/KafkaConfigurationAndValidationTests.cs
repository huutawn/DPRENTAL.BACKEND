using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.DependencyInjection;
using Dprental.RentalService.Application.Kafka.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaConfigurationAndValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void KafkaOptions_DisabledByDefault_PassesValidationWithoutBootstrapServers()
    {
        var options = new KafkaOptions
        {
            Enabled = false,
            BootstrapServers = string.Empty
        };

        var validator = new KafkaOptionsValidator();
        var result = validator.Validate("Kafka", options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void KafkaOptions_EnabledWithoutBootstrapServers_FailsValidation()
    {
        var options = new KafkaOptions
        {
            Enabled = true,
            BootstrapServers = string.Empty
        };

        var validator = new KafkaOptionsValidator();
        var result = validator.Validate("Kafka", options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("BootstrapServers"));
    }

    [Fact]
    public void KafkaOptions_EnabledWithInvalidTimeoutsOrTopics_FailsValidation()
    {
        var options = new KafkaOptions
        {
            Enabled = true,
            BootstrapServers = "127.0.0.1:9092",
            RentalEventsTopic = "",
            DeliveryTimeoutSeconds = 0,
            LeaseDurationSeconds = -5,
            MaxBatchPerRound = 0
        };

        var validator = new KafkaOptionsValidator();
        var result = validator.Validate("Kafka", options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("RentalEventsTopic"));
        Assert.Contains(result.Failures!, f => f.Contains("DeliveryTimeoutSeconds"));
        Assert.Contains(result.Failures!, f => f.Contains("LeaseDurationSeconds"));
        Assert.Contains(result.Failures!, f => f.Contains("MaxBatchPerRound"));
    }

    [Fact]
    public void KafkaOptions_SaslEnabled_WithoutCredentials_FailsValidation()
    {
        var options = new KafkaOptions
        {
            Enabled = true,
            BootstrapServers = "127.0.0.1:9092",
            SecurityProtocol = Confluent.Kafka.SecurityProtocol.SaslPlaintext,
            SaslMechanism = null,
            SaslUsername = null,
            SaslPassword = null
        };

        var validator = new KafkaOptionsValidator();
        var result = validator.Validate("Kafka", options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("SaslMechanism"));
        Assert.Contains(result.Failures!, f => f.Contains("SaslUsername"));
        Assert.Contains(result.Failures!, f => f.Contains("SaslPassword"));
    }

    [Fact]
    public void KafkaOptions_StartupValidation_ThrowsOptionsValidationException_WhenEnabledAndInvalid()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Kafka:Enabled", "true"),
                new KeyValuePair<string, string?>("Kafka:BootstrapServers", "")
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKafka(config);

        var sp = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
        {
            _ = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
        });
    }

    [Fact]
    public void ProposedCommands_SerializeWithUnifiedEnvelope_AndPreserveContract()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var holdRequest = new CreateHoldRequest(
            ListingId: Guid.NewGuid(),
            Quantity: 2,
            StartDate: new DateOnly(2026, 10, 20),
            EndDate: new DateOnly(2026, 10, 24),
            HoldExpiresAt: now.AddMinutes(30),
            SourceSystem: "rental",
            GroupReference: $"rental-item:{itemId}");

        var command = new CreateHoldCommandPayload(
            orderId,
            itemId,
            "op-hold-1",
            holdRequest.ListingId,
            holdRequest.Quantity,
            holdRequest.StartDate,
            holdRequest.EndDate,
            holdRequest.HoldExpiresAt,
            "Notes");

        var envelope = new KafkaMessageEnvelope<CreateHoldCommandPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.CommandTypes.CreateHold,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            orderId.ToString("D"),
            now,
            "corr-1",
            null,
            command);

        string json = JsonSerializer.Serialize(envelope, JsonOptions);

        Assert.Contains("\"messageType\":\"CreateHold\"", json);
        Assert.Contains($"\"rentalOrderId\":\"{orderId}\"", json);
        Assert.Contains($"\"rentalItemId\":\"{itemId}\"", json);
        Assert.Contains("\"quantity\":2", json);

        var deserialized = JsonSerializer.Deserialize<KafkaMessageEnvelope<CreateHoldCommandPayload>>(json, JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(orderId, deserialized.Payload.RentalOrderId);
        Assert.Equal(2, deserialized.Payload.Quantity);
    }

    [Fact]
    public void RentalOrderCreated_DoesNotExposePhysicalIdentityOrPersonalSnapshot()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var item = new RentalOrderCreatedItemPayload(
            RentalItemId: Guid.NewGuid(),
            EquipmentListingId: Guid.NewGuid(),
            SupplierId: Guid.NewGuid(),
            Quantity: 1,
            ExpectedStartDate: new DateOnly(2026, 10, 15),
            ExpectedEndDate: new DateOnly(2026, 10, 18),
            RentalAmount: 300_000m,
            DepositAmount: 500_000m);

        var payload = new RentalOrderCreatedPayload(
            RentalOrderId: orderId,
            OrderCode: "ORD-KAF-001",
            RenterId: renterId,
            Currency: "VND",
            TotalRentalAmount: 300_000m,
            TotalDepositAmount: 500_000m,
            TotalPayableAmount: 800_000m,
            PaymentDeadline: now.AddMinutes(30),
            CreatedAt: now,
            Items: [item]);

        string json = JsonSerializer.Serialize(payload, JsonOptions);

        // Security invariant: Exclude physical identity and personal contact data
        Assert.DoesNotContain("assetId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serialNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assetCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipientPhone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shippingAddress", json, StringComparison.OrdinalIgnoreCase);

        // Required business amounts and dates are preserved
        Assert.Contains("\"totalPayableAmount\":800000", json);
        Assert.Contains("\"orderCode\":\"ORD-KAF-001\"", json);
    }
}
