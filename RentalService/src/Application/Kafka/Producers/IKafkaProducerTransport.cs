using Confluent.Kafka;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>Giao tiếp broker cho publisher; trả delivery result để caller kiểm tra Persisted trước khi chốt Outbox.</summary>
public interface IKafkaProducerTransport : IDisposable
{
    Task<DeliveryResult<string, string>> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default);

    void Flush(TimeSpan timeout);
}
