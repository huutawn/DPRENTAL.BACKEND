using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Kafka.Producers;

/// <summary>
/// Triển khai Kafka producer sử dụng Confluent.Kafka ProducerBuilder với xác nhận PersistenceStatus.Persisted.
/// </summary>
public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducer>? _logger;
    private bool _disposed;

    public KafkaProducer(
        IConfiguration configuration,
        ILogger<KafkaProducer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new InvalidOperationException("Kafka BootstrapServers configuration (Kafka:BootstrapServers) is required.");
        }

        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public KafkaProducer(
        IProducer<string, string> producer,
        ILogger<KafkaProducer>? logger = null)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _logger = logger;
    }

    public async Task<bool> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var message = new Message<string, string>
            {
                Key = partitionKey,
                Value = payload
            };

            var deliveryResult = await _producer.ProduceAsync(topic, message, cancellationToken);
            return deliveryResult.Status == PersistenceStatus.Persisted;
        }
        catch (ProduceException<string, string> ex)
        {
            _logger?.LogWarning(ex, "ProduceException publishing to topic {Topic}", topic);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
            _producer.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error disposing Kafka producer");
        }
    }
}
