using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Dprental.RecommendationService.Application.Kafka.Producer;

public sealed class KafkaDlqProducer : IKafkaDlqProducer
{
    private readonly IProducer<string?, string> _producer;
    private readonly ILogger<KafkaDlqProducer> _logger;

    public KafkaDlqProducer(
        IKafkaClientFactory clientFactory,
        ILogger<KafkaDlqProducer> logger)
    {
        _logger = logger;
        _producer = clientFactory.CreateProducer();
    }

    public async Task ProduceToDlqAsync(
        string dlqTopic,
        string? key,
        string value,
        string reason,
        string sourceTopic,
        int sourcePartition,
        long sourceOffset,
        CancellationToken cancellationToken)
    {
        var headers = new Headers
        {
            { "x-dlq-error-reason", Encoding.UTF8.GetBytes(reason) },
            { "x-dlq-source-topic", Encoding.UTF8.GetBytes(sourceTopic) },
            { "x-dlq-source-partition", Encoding.UTF8.GetBytes(sourcePartition.ToString(CultureInfo.InvariantCulture)) },
            { "x-dlq-source-offset", Encoding.UTF8.GetBytes(sourceOffset.ToString(CultureInfo.InvariantCulture)) },
            { "x-dlq-timestamp", Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) }
        };

        var message = new Message<string?, string>
        {
            Key = key,
            Value = value,
            Headers = headers
        };

        var deliveryReport = await _producer.ProduceAsync(dlqTopic, message, cancellationToken);
        _logger.LogInformation(
            "Produced record to DLQ topic {DlqTopic} at partition {Partition} offset {Offset}. Source: {SourceTopic}:{SourcePartition}@{SourceOffset}, Reason: {Reason}",
            dlqTopic, deliveryReport.Partition.Value, deliveryReport.Offset.Value, sourceTopic, sourcePartition, sourceOffset, reason);
    }

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
            _producer.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing Kafka DLQ producer.");
        }
    }
}
