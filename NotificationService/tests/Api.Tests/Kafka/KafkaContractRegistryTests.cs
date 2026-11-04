using System.Text.Json;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Kafka;

[Trait("Category", "Unit")]
public sealed class KafkaContractRegistryTests
{
    private static readonly IKafkaEventHandler[] AllHandlers =
    {
        new RentalOrderCreatedHandler(),
        new RentalOrderPaidHandler(),
        new RentalSupplierConfirmationRequestedHandler(),
        new RentalOrderExpiredHandler(),
        new RentalOrderCancelledHandler(),
        new RentalItemHandoverCompletedHandler(),
        new RentalHandoverOverdueHandler(),
        new ReturnConfirmationOverdueHandler(),
        new RentalOrderCompletedHandler(),
        new LatePaymentRefundRequestedHandler(),
        new RentalItemConfirmedHandler(),
        new RentalItemRejectedHandler(),
        new RefundCompletedFinancialHandler(),
        new SupplierPayoutCompletedHandler()
    };

    private static KafkaContractRegistry CreateRegistry() => new(AllHandlers);

    private static KafkaMessageEnvelope CreateEnvelope(
        string messageType = "RentalOrderCreated",
        string producer = "RentalService",
        int version = 1) =>
        new()
        {
            MessageId = Guid.NewGuid(),
            MessageType = messageType,
            Producer = producer,
            Version = version,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { id = 1 })
        };

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void IsVersionSupported_OnlyVersion1IsSupported(int version, bool expected)
    {
        var registry = CreateRegistry();
        Assert.Equal(expected, registry.IsVersionSupported(version));
    }

    [Theory]
    [InlineData("rental.events.v1", true)]
    [InlineData("payment.analytics.v1", true)]
    [InlineData("rental.events.v2", false)]
    [InlineData("payment.events.v1", false)]
    [InlineData("unknown.topic", false)]
    public void IsAllowedTopic_OnlyAuthorizedTopicsAllowed(string topic, bool expected)
    {
        var registry = CreateRegistry();
        Assert.Equal(expected, registry.IsAllowedTopic(topic));
    }

    [Theory]
    [InlineData("RentalService", true)]
    [InlineData("rental-service", true)]
    [InlineData("PaymentService", true)]
    [InlineData("payment-service", true)]
    [InlineData("ReviewService", false)]
    [InlineData("UnknownService", false)]
    public void IsAllowedProducer_OnlyAuthorizedProducersAllowed(string producer, bool expected)
    {
        var registry = CreateRegistry();
        Assert.Equal(expected, registry.IsAllowedProducer(producer));
    }

    [Theory]
    [InlineData("rental.events.v1", "RentalService", true)]
    [InlineData("rental.events.v1", "rental-service", true)]
    [InlineData("rental.events.v1", "PaymentService", false)]
    [InlineData("rental.events.v1", "payment-service", false)]
    [InlineData("payment.analytics.v1", "PaymentService", true)]
    [InlineData("payment.analytics.v1", "payment-service", true)]
    [InlineData("payment.analytics.v1", "RentalService", false)]
    [InlineData("payment.analytics.v1", "rental-service", false)]
    public void IsTopicProducerAllowed_EnforcesTopicProducerExclusivity(string topic, string producer, bool expected)
    {
        var registry = CreateRegistry();
        Assert.Equal(expected, registry.IsTopicProducerAllowed(topic, producer));
    }

    [Fact]
    public void Resolve_ValidRentalEvent_ReturnsHandledStatusWithHandler()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderCreated", "RentalService", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Handled, resolution.Status);
        Assert.NotNull(resolution.Handler);
        Assert.Equal("RentalOrderCreated", resolution.Handler.EventType);
    }

    [Fact]
    public void Resolve_KebabCaseRentalService_ReturnsHandledStatus()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderPaid", "rental-service", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Handled, resolution.Status);
        Assert.NotNull(resolution.Handler);
        Assert.Equal("RentalOrderPaid", resolution.Handler.EventType);
    }

    [Theory]
    [InlineData("RefundCompletedFinancial")]
    [InlineData("SupplierPayoutCompleted")]
    public void Resolve_PaymentAnalyticsEvents_HandledOnPaymentAnalyticsTopic(string messageType)
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope(messageType, "PaymentService", 1);

        var resolution = registry.Resolve("payment.analytics.v1", env);

        Assert.Equal(ContractResolutionStatus.Handled, resolution.Status);
        Assert.NotNull(resolution.Handler);
        Assert.Equal(messageType, resolution.Handler.EventType);
    }

    [Fact]
    public void Resolve_PaymentServiceOnRentalTopic_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RefundCompletedFinancial", "PaymentService", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("INVALID_PRODUCER_FOR_TOPIC", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_RentalServiceOnPaymentAnalyticsTopic_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderCreated", "RentalService", 1);

        var resolution = registry.Resolve("payment.analytics.v1", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("INVALID_PRODUCER_FOR_TOPIC", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_UnsupportedVersion_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderCreated", "RentalService", 2);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("UNSUPPORTED_VERSION", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_UnauthorizedProducer_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderCreated", "ExternalFraudService", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("INVALID_PRODUCER", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_UnauthorizedTopic_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalOrderCreated", "RentalService", 1);

        var resolution = registry.Resolve("arbitrary.events.topic", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("INVALID_TOPIC", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_ValidTopicAndProducerWithUnhandledMessageType_ReturnsIgnored()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("RentalDisputeOpened", "RentalService", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Ignored, resolution.Status);
        Assert.Null(resolution.Handler);
    }

    [Fact]
    public void Resolve_ValidPaymentTopicAndProducerWithUnhandledMessageType_ReturnsIgnored()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("PaymentInvoiceGenerated", "PaymentService", 1);

        var resolution = registry.Resolve("payment.analytics.v1", env);

        Assert.Equal(ContractResolutionStatus.Ignored, resolution.Status);
        Assert.Null(resolution.Handler);
    }

    [Fact]
    public void Resolve_MissingMessageType_Quarantined()
    {
        var registry = CreateRegistry();
        var env = CreateEnvelope("", "RentalService", 1);

        var resolution = registry.Resolve("rental.events.v1", env);

        Assert.Equal(ContractResolutionStatus.Quarantined, resolution.Status);
        Assert.Equal("MISSING_MESSAGE_TYPE", resolution.ErrorCode);
    }
}
