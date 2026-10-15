#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class KafkaProducerTransport : IKafkaProducerTransport
{
    private readonly IProducer<string, string>? _producer;
    private readonly ILogger<KafkaProducerTransport> _logger;
    private bool _disposed;

    public KafkaProducerTransport(
        IOptions<KafkaOptions> options,
        ILogger<KafkaProducerTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var kafkaOptions = options.Value;
        if (!kafkaOptions.Enabled)
        {
            _logger.LogInformation("Kafka is disabled in configuration; KafkaProducerTransport is inactive");
            return;
        }

        if (string.IsNullOrWhiteSpace(kafkaOptions.BootstrapServers))
        {
            throw new InvalidOperationException("Kafka BootstrapServers must be configured when Kafka is enabled.");
        }

        var config = new ProducerConfig
        {
            BrokerAddressFamily = BrokerAddressFamily.V4,
            BootstrapServers = kafkaOptions.BootstrapServers,
            ClientId = kafkaOptions.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 100,
            MessageTimeoutMs = kafkaOptions.DeliveryTimeoutSeconds * 1000
        };

        if (kafkaOptions.SecurityProtocol.HasValue)
        {
            config.SecurityProtocol = kafkaOptions.SecurityProtocol.Value;
        }

        if (kafkaOptions.SaslMechanism.HasValue)
        {
            config.SaslMechanism = kafkaOptions.SaslMechanism.Value;
        }

        if (!string.IsNullOrWhiteSpace(kafkaOptions.SaslUsername))
        {
            config.SaslUsername = kafkaOptions.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(kafkaOptions.SaslPassword))
        {
            config.SaslPassword = kafkaOptions.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(kafkaOptions.SslCaLocation))
        {
            config.SslCaLocation = kafkaOptions.SslCaLocation;
        }

        if (!string.IsNullOrWhiteSpace(kafkaOptions.SslCertificateLocation))
        {
            config.SslCertificateLocation = kafkaOptions.SslCertificateLocation;
        }

        if (!string.IsNullOrWhiteSpace(kafkaOptions.SslKeyLocation))
        {
            config.SslKeyLocation = kafkaOptions.SslKeyLocation;
        }

        _producer = new ProducerBuilder<string, string>(config).Build();
        _logger.LogInformation("KafkaProducerTransport initialized with BootstrapServers={Servers}, ClientId={ClientId}",
            kafkaOptions.BootstrapServers, kafkaOptions.ClientId);
    }

    // Constructor for testing with mock/fake producer
    public KafkaProducerTransport(
        IProducer<string, string> producer,
        ILogger<KafkaProducerTransport> logger)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DeliveryResult<string, string>> ProduceAsync(
        string topic,
        string partitionKey,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_producer == null)
        {
            throw new InvalidOperationException("Kafka producer transport is disabled or not configured.");
        }

        return await _producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = partitionKey,
            Value = payload
        }, cancellationToken);
    }

    public void Flush(TimeSpan timeout)
    {
        if (_disposed) return;
        _producer?.Flush(timeout);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _producer?.Flush(TimeSpan.FromSeconds(5));
            _producer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disposing Kafka producer");
        }
    }
}
