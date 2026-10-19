using Confluent.Kafka;

namespace Dprental.RecommendationService.Application.Kafka;

public interface IKafkaClientFactory
{
    IConsumer<string?, string> CreateConsumer(
        string groupId,
        string clientId,
        Action<IConsumer<string?, string>, List<TopicPartition>>? partitionsAssigned = null,
        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? partitionsRevoked = null,
        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? partitionsLost = null,
        Action<IConsumer<string?, string>, Error>? errorHandler = null);

    IProducer<string?, string> CreateProducer();
}
