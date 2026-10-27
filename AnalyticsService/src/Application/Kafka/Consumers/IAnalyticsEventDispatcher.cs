namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>Xử lý trọn vẹn một message Kafka: kiểm tra, chống trùng, chiếu vào read model và lưu.</summary>
public interface IAnalyticsEventDispatcher
{
    Task<DispatchResult> DispatchAsync(KafkaRecord record, CancellationToken cancellationToken = default);
}
