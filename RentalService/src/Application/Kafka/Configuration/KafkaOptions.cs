using Confluent.Kafka;

namespace Dprental.RentalService.Application.Kafka.Configuration;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Kafka is disabled by default. Must be explicitly set to true to enable workers and broker transport.
    /// </summary>
    public bool Enabled { get; set; }

    public string BootstrapServers { get; set; } = string.Empty;

    public string ClientId { get; set; } = "rental-service";

    public string ConsumerGroupId { get; set; } = "rental-payment-events-consumer";

    // Standard existing topics
    public string RentalEventsTopic { get; set; } = "rental.events.v1";
    public string PaymentEventsTopic { get; set; } = "payment.events.v1";
    public string PaymentDlqTopic { get; set; } = "payment.events.v1.dlq";

    // PROPOSED topics for checkout preparation
    public string InventoryCommandsTopic { get; set; } = "inventory.commands.v1";
    public string PaymentCommandsTopic { get; set; } = "payment.commands.v1";

    // Security options
    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
    public string? SslCertificateLocation { get; set; }
    public string? SslKeyLocation { get; set; }

    // Timeouts and Polling
    public int DeliveryTimeoutSeconds { get; set; } = 10;
    public int LeaseDurationSeconds { get; set; } = 30;
    public int MaxBatchPerRound { get; set; } = 50;
    public int OutboxPollingIntervalSeconds { get; set; } = 15;
    public int InboxPollingIntervalSeconds { get; set; } = 15;
    public int ShutdownTimeoutSeconds { get; set; } = 15;
    public int ConsumeTimeoutMilliseconds { get; set; } = 500;
}
