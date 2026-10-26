using Confluent.Kafka;

namespace Dprental.AnalyticsService.Application.Kafka.Configuration;

/// <summary>
/// Cấu hình kết nối Kafka để AnalyticsService tiêu thụ event của Rental và Payment.
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Kafka tắt mặc định. Phải đặt true tường minh thì consumer mới chạy.
    /// </summary>
    public bool Enabled { get; set; }

    public string BootstrapServers { get; set; } = string.Empty;

    public string ClientId { get; set; } = "analytics-service";

    /// <summary>
    /// Consumer group riêng của Analytics, độc lập với consumer group của các service khác.
    /// </summary>
    public string ConsumerGroupId { get; set; } = "analytics-service";

    public string RentalEventsTopic { get; set; } = "rental.events.v1";

    public string PaymentEventsTopic { get; set; } = "payment.events.v1";

    public SecurityProtocol? SecurityProtocol { get; set; }

    public SaslMechanism? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }

    public string? SslCaLocation { get; set; }

    /// <summary>
    /// Lỗi tạm thời (DB) thì thử lại chính message đó, chờ tăng dần: 500ms, 1s, 2s... tối đa RetryMaxDelaySeconds.
    /// </summary>
    public int RetryInitialDelayMilliseconds { get; set; } = 500;

    public int RetryMaxDelaySeconds { get; set; } = 30;
}
