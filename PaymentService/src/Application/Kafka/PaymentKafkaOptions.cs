using Confluent.Kafka;

namespace Dprental.PaymentService.Application.Kafka;

public sealed class PaymentKafkaOptions
{
    public const string SectionName = "Kafka";
    public bool Enabled { get; set; }
    public string BootstrapServers { get; set; } = string.Empty;
    public string ClientId { get; set; } = "payment-service";
    public string PaymentEventsTopic { get; set; } = "payment.events.v1";
    public int PollIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 50;
    public int DeliveryTimeoutSeconds { get; set; } = 10;
    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
}
