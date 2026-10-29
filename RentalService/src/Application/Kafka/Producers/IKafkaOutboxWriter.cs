using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>Đường ghi envelope vào Outbox dùng chung cho producer; không gửi broker hoặc tự commit transaction.</summary>
public interface IKafkaOutboxWriter
{
    Task<Result<ProducerEnqueueResult>> EnqueueAsync<TPayload>(
        string topic,
        string partitionKey,
        KafkaMessageEnvelope<TPayload> candidateEnvelope,
        CancellationToken cancellationToken = default);
}
