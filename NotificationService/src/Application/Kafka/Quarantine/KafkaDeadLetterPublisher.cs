using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Kafka.Quarantine;

public sealed class KafkaDeadLetterPublisher : IKafkaDeadLetterPublisher, IDisposable
{
    private readonly ILogger<KafkaDeadLetterPublisher> _logger;
    private readonly string _dlqTopic;
    private readonly IProducer<string, string>? _producer;

    public KafkaDeadLetterPublisher(IConfiguration configuration, ILogger<KafkaDeadLetterPublisher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dlqTopic = configuration["Kafka:DlqTopic"] ?? "rental.events.v1.notification.dlq";

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true
            };
            _producer = new ProducerBuilder<string, string>(config).Build();
        }
    }

    public async Task PublishToDeadLetterQueueAsync(
        string rawMessage,
        string quarantineId,
        string reason,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = new
        {
            quarantineId,
            reason,
            errorCode,
            quarantinedAt = DateTimeOffset.UtcNow,
            payload = rawMessage
        };

        var messageJson = JsonSerializer.Serialize(envelope);

        _logger.LogWarning(
            "Đẩy message vào DLQ: QuarantineId={QuarantineId}, Reason={Reason}, ErrorCode={ErrorCode}",
            quarantineId,
            reason,
            errorCode);

        if (_producer != null)
        {
            var message = new Message<string, string>
            {
                Key = quarantineId,
                Value = messageJson
            };
            await _producer.ProduceAsync(_dlqTopic, message, cancellationToken);
        }
    }

    public void Dispose()
    {
        _producer?.Dispose();
    }
}
