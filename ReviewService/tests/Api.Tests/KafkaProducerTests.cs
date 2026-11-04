using Confluent.Kafka;
using Dprental.ReviewService.Application.Kafka.Producers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class KafkaProducerTests
{
    [Fact]
    public void KafkaProducer_ThrowsInvalidOperationException_WhenBootstrapServersMissing()
    {
        var inMemorySettings = new Dictionary<string, string?>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new KafkaProducer(configuration));
        Assert.Contains("Kafka:BootstrapServers", exception.Message);
    }

    [Fact]
    public void KafkaProducer_ThrowsInvalidOperationException_WhenBootstrapServersEmpty()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "   "
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new KafkaProducer(configuration));
        Assert.Contains("Kafka:BootstrapServers", exception.Message);
    }

    [Fact]
    public async Task KafkaProducer_ProduceAsync_ReturnsTrue_WhenStatusIsPersisted()
    {
        var mockProducer = new StubProducer
        {
            DeliveryResultToReturn = new DeliveryResult<string, string>
            {
                Status = PersistenceStatus.Persisted,
                Topic = "test-topic",
                Offset = new Offset(1)
            }
        };

        using var kafkaProducer = new KafkaProducer(mockProducer);

        var result = await kafkaProducer.ProduceAsync("test-topic", "key-1", "{\"msg\":1}");
        Assert.True(result);
        Assert.Equal("test-topic", mockProducer.LastTopic);
        Assert.Equal("key-1", mockProducer.LastMessage?.Key);
        Assert.Equal("{\"msg\":1}", mockProducer.LastMessage?.Value);
    }

    [Fact]
    public async Task KafkaProducer_ProduceAsync_ReturnsFalse_WhenStatusIsNotPersisted()
    {
        var mockProducer = new StubProducer
        {
            DeliveryResultToReturn = new DeliveryResult<string, string>
            {
                Status = PersistenceStatus.NotPersisted,
                Topic = "test-topic"
            }
        };

        using var kafkaProducer = new KafkaProducer(mockProducer);

        var result = await kafkaProducer.ProduceAsync("test-topic", "key-2", "{\"msg\":2}");
        Assert.False(result);
    }

    [Fact]
    public async Task KafkaProducer_ProduceAsync_ReturnsFalse_WhenProduceExceptionThrown()
    {
        var mockProducer = new StubProducer
        {
            ExceptionToThrow = new ProduceException<string, string>(
                new Error(ErrorCode.BrokerNotAvailable, "Broker not available"),
                new DeliveryResult<string, string> { Status = PersistenceStatus.NotPersisted })
        };

        using var kafkaProducer = new KafkaProducer(mockProducer);

        var result = await kafkaProducer.ProduceAsync("test-topic", "key-3", "{\"msg\":3}");
        Assert.False(result);
    }

    private sealed class StubProducer : IProducer<string, string>
    {
        public DeliveryResult<string, string>? DeliveryResultToReturn { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public string? LastTopic { get; private set; }
        public Message<string, string>? LastMessage { get; private set; }

        public Handle Handle => throw new NotImplementedException();
        public string Name => "StubProducer";

        public Task<DeliveryResult<string, string>> ProduceAsync(
            string topic,
            Message<string, string> message,
            CancellationToken cancellationToken = default)
        {
            LastTopic = topic;
            LastMessage = message;

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(DeliveryResultToReturn ?? new DeliveryResult<string, string>
            {
                Status = PersistenceStatus.Persisted
            });
        }

        public Task<DeliveryResult<string, string>> ProduceAsync(
            TopicPartition topicPartition,
            Message<string, string> message,
            CancellationToken cancellationToken = default)
        {
            return ProduceAsync(topicPartition.Topic, message, cancellationToken);
        }

        public void Produce(string topic, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null)
        {
        }

        public void Produce(TopicPartition topicPartition, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null)
        {
        }

        public int Poll(TimeSpan timeout) => 0;
        public int Flush(TimeSpan timeout) => 0;
        public void Flush(CancellationToken cancellationToken = default) { }
        public void InitTransactions(TimeSpan timeout) { }
        public void BeginTransaction() { }
        public void CommitTransaction(TimeSpan timeout) { }
        public void AbortTransaction(TimeSpan timeout) { }
        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) { }
        public void CommitTransaction() { }
        public void AbortTransaction() { }
        public int AddBrokers(string brokers) => 0;
        public void SetSaslCredentials(string username, string password) { }
        public void Dispose() { }
    }
}
