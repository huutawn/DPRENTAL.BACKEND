namespace Dprental.ReviewService.Application.Kafka.Producers;

/// <summary>
/// Hợp đồng xuất bản thông điệp Kafka của ReviewService.
/// </summary>
public interface IKafkaProducer
{
    Task<bool> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Triển khai mặc định khi chưa cấu hình Kafka broker thật.
/// </summary>
public sealed class NullKafkaProducer : IKafkaProducer
{
    public Task<bool> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
