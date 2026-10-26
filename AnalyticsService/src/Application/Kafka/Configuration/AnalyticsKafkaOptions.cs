using Confluent.Kafka;

namespace Dprental.AnalyticsService.Application.Kafka.Configuration;

/// <summary>
/// Cấu hình Kafka của Analytics (section "Kafka"). Giữ nguyên các khóa Bảo đã dùng để không đổi cấu hình
/// môi trường; bổ sung cấu hình thử lại. Được kiểm tra lúc khởi động bởi <see cref="AnalyticsKafkaOptionsValidator"/>.
/// </summary>
public sealed class AnalyticsKafkaOptions
{
    public const string SectionName = "Kafka";

    public bool Enabled { get; set; }
    public string BootstrapServers { get; set; } = string.Empty;
    public string GroupId { get; set; } = "analytics-dashboard-v1";
    public string RentalTopic { get; set; } = "rental.events.v1";
    public string PaymentTopic { get; set; } = "payment.analytics.v1";

    // Proof ký số của Payment cho từng message trên PaymentTopic
    public string ProofIssuer { get; set; } = "dprental-integrity-host";
    public string ProofEnvironment { get; set; } = "production";
    public Dictionary<string, string> ProofPublicKeys { get; set; } = new(StringComparer.Ordinal);

    public SecurityProtocol? SecurityProtocol { get; set; }
    public SaslMechanism? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
    public string? SslCaLocation { get; set; }

    // Lỗi tạm thời (DB, thiếu key proof) → chờ 500ms, 1s, 2s... tối đa RetryMaxDelaySeconds rồi thử lại CHÍNH message đó
    public int RetryInitialDelayMilliseconds { get; set; } = 500;
    public int RetryMaxDelaySeconds { get; set; } = 30;
}
