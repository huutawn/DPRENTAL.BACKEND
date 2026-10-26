namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Một message lấy từ Kafka: vị trí (topic, partition, offset) và nội dung JSON gốc.
/// Worker (B6) tạo record này; dispatcher không phụ thuộc thư viện Kafka.
/// </summary>
public sealed record KafkaRecord(string Topic, int Partition, long Offset, string Value);
