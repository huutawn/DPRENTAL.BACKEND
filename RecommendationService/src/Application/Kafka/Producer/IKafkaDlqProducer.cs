namespace Dprental.RecommendationService.Application.Kafka.Producer;

public interface IKafkaDlqProducer : IDisposable
{
    Task ProduceToDlqAsync(
        string dlqTopic,
        string? key,
        string value,
        string reason,
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        CancellationToken cancellationToken);
}
