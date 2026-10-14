using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

public interface IKafkaOutboxWriter
{
    Task<Result<ProducerEnqueueResult>> EnqueueAsync<TPayload>(
        string topic,
        string partitionKey,
        KafkaMessageEnvelope<TPayload> candidateEnvelope,
        CancellationToken cancellationToken = default);
}
