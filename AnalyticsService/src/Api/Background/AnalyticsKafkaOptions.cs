using Confluent.Kafka;

namespace Dprental.AnalyticsService.Api.Background;

public sealed class AnalyticsKafkaOptions
{
    public bool Enabled { get; set; }
    public string BootstrapServers { get; set; } = string.Empty;
    public string GroupId { get; set; } = "analytics-dashboard-v1";
    public string RentalTopic { get; set; } = "rental.events.v1";
    public string PaymentTopic { get; set; } = "payment.analytics.v1";
    public string ProofIssuer { get; set; } = "dprental-integrity-host";
    public string ProofEnvironment { get; set; } = "production";
    public Dictionary<string, string> ProofPublicKeys { get; set; } = new(StringComparer.Ordinal);
    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }
}
