using Confluent.Kafka;

namespace Dprental.RentalService.Application.Kafka.Producers;

public interface IKafkaProducerTransport : IDisposable
{
    Task<DeliveryResult<string, string>> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default);

    void Flush(TimeSpan timeout);
}
