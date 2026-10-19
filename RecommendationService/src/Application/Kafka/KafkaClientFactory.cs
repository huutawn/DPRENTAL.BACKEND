using Confluent.Kafka;
using Dprental.RecommendationService.Application.Kafka.Options;
using Microsoft.Extensions.Options;

namespace Dprental.RecommendationService.Application.Kafka;

public sealed class KafkaClientFactory : IKafkaClientFactory
{
    private readonly KafkaOptions _options;

    public KafkaClientFactory(IOptions<KafkaOptions> options)
    {
        _options = options.Value;
    }

    public IConsumer<string?, string> CreateConsumer(
        string groupId,
        string clientId,
        Action<IConsumer<string?, string>, List<TopicPartition>>? partitionsAssigned = null,
        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? partitionsRevoked = null,
        Action<IConsumer<string?, string>, List<TopicPartitionOffset>>? partitionsLost = null,
        Action<IConsumer<string?, string>, Error>? errorHandler = null)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = groupId,
            ClientId = clientId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            SessionTimeoutMs = _options.SessionTimeoutMs,
            MaxPollIntervalMs = _options.MaxPollIntervalMs,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        };

        var builder = new ConsumerBuilder<string?, string>(consumerConfig);

        if (partitionsAssigned != null)
        {
            builder.SetPartitionsAssignedHandler(partitionsAssigned);
        }

        if (partitionsRevoked != null)
        {
            builder.SetPartitionsRevokedHandler(partitionsRevoked);
        }

        if (partitionsLost != null)
        {
            builder.SetPartitionsLostHandler(partitionsLost);
        }

        if (errorHandler != null)
        {
            builder.SetErrorHandler(errorHandler);
        }

        return builder.Build();
    }

    public IProducer<string?, string> CreateProducer()
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 5,
            RetryBackoffMs = 100
        };

        return new ProducerBuilder<string?, string>(producerConfig).Build();
    }
}
