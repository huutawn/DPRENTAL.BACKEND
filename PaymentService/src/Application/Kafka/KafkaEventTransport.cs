using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Kafka;

public sealed class KafkaEventTransport : IEventTransport, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventTransport(IOptions<PaymentKafkaOptions> options)
    {
        var config = options.Value;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.BootstrapServers))
            throw new InvalidOperationException("Kafka producer requires Kafka:Enabled and BootstrapServers.");

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config.BootstrapServers,
            ClientId = config.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = Math.Max(1, config.DeliveryTimeoutSeconds) * 1000
        };
        if (config.SecurityProtocol.HasValue)
            producerConfig.SecurityProtocol = config.SecurityProtocol.Value;
        if (config.SaslMechanism.HasValue)
            producerConfig.SaslMechanism = config.SaslMechanism.Value;
        if (!string.IsNullOrWhiteSpace(config.SaslUsername))
            producerConfig.SaslUsername = config.SaslUsername;
        if (!string.IsNullOrWhiteSpace(config.SaslPassword))
            producerConfig.SaslPassword = config.SaslPassword;
        if (!string.IsNullOrWhiteSpace(config.SslCaLocation))
            producerConfig.SslCaLocation = config.SslCaLocation;

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    public async Task PublishAsync(string topic, string partitionKey, string payload,
        CancellationToken cancellationToken)
    {
        var result = await _producer.ProduceAsync(topic,
            new Message<string, string> { Key = partitionKey, Value = payload },
            cancellationToken);
        if (result.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException($"Kafka did not confirm persistence: {result.Status}");
    }

    public void Dispose() => _producer.Dispose();
}
