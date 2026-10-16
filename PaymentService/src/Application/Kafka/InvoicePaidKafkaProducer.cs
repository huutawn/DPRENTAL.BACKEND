#pragma warning disable CA1848
using System;
using Confluent.Kafka;
using Dprental.PaymentService.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Kafka;

public sealed class InvoicePaidKafkaProducer : IInvoicePaidKafkaProducer, IDisposable
{
    private readonly IProducer<string, string>? _producer;
    private readonly ILogger<InvoicePaidKafkaProducer> _logger;
    private readonly bool _enabled;

    public InvoicePaidKafkaProducer(
        IOptions<PaymentKafkaOptions> options,
        ILogger<InvoicePaidKafkaProducer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);

        var config = options.Value;
        _enabled = config.Enabled && !string.IsNullOrWhiteSpace(config.BootstrapServers);

        if (_enabled)
        {
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                ClientId = config.ClientId,
                EnableIdempotence = true,
                Acks = Acks.All,
                MessageTimeoutMs = config.DeliveryTimeoutSeconds * 1000,
                SocketTimeoutMs = config.DeliveryTimeoutSeconds * 1000
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
    }

    public async Task ProduceAsync(string topic, string key, string payload, CancellationToken cancellationToken)
    {
        if (!_enabled || _producer == null)
        {
            throw new InvalidOperationException("Kafka producer is disabled or not configured.");
        }

        var message = new Message<string, string>
        {
            Key = key,
            Value = payload,
            Timestamp = new Timestamp(DateTime.UtcNow)
        };

        var delivery = await _producer.ProduceAsync(topic, message, cancellationToken);
        if (delivery.Status != PersistenceStatus.Persisted)
            throw new InvalidOperationException($"Kafka did not confirm persistence: {delivery.Status}");
    }

    public void Dispose()
    {
        try
        {
            _producer?.Flush(TimeSpan.FromSeconds(5));
            _producer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing Kafka producer.");
        }
    }
}
