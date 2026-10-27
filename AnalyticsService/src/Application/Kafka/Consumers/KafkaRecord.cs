namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Một message lấy từ Kafka: vị trí, key, nội dung JSON gốc và header proof (chỉ Payment có).
/// Worker tạo record này; phần xử lý không phụ thuộc thư viện Kafka nên test được không cần broker.
/// </summary>
public sealed record KafkaRecord(string Topic, int Partition, long Offset, string? Key, string Value, string? ProofHeader = null);
