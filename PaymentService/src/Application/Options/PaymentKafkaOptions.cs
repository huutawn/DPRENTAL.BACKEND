using Confluent.Kafka;

namespace Dprental.PaymentService.Application.Options;

public sealed class PaymentKafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;
    public string Topic { get; set; } = "payment.events.v1";
    public int DeliveryTimeoutSeconds { get; set; } = 10;
    public int LeaseDurationSeconds { get; set; } = 30;
    public int PollingIntervalSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 10;
    public int MaxBackoffSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 20;
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "payment-service";
    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
}
