namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed record ProducerEnqueueResult(
    Guid MessageId,
    bool IsReplay,
    string MessageType,
    string Topic,
    string PartitionKey);
