using System.Text;
using Confluent.Kafka;
using Dprental.InventoryService.Domain.Kafka.Contracts;
using Dprental.InventoryService.Domain.Kafka.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Domain.Kafka.Transport;

/// <summary>
/// Triển khai IKafkaProducer sử dụng Confluent.Kafka Client với cấu hình Idempotent và Acks=All.
/// Chỉ có lớp này và các transport workers tham chiếu thư viện Confluent.Kafka.
/// </summary>
public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducer>? _logger;
    private bool _disposed;

    public KafkaProducer(IOptions<KafkaOptions> options, ILogger<KafkaProducer>? logger = null)
    {
        _logger = logger;
        var config = options.Value;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config.BootstrapServers,
            ClientId = config.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = config.DeliveryTimeoutMs,
            MessageSendMaxRetries = int.MaxValue,
            RetryBackoffMs = 100
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    /// <summary>
    /// Khởi tạo với IProducer đã cấu hình (dùng cho testing/mocking).
    /// </summary>
    public KafkaProducer(IProducer<string, string> producer, ILogger<KafkaProducer>? logger = null)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ProduceRawAsync(
        string topic,
        string kafkaKey,
        string payloadJson,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(kafkaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var message = new Message<string, string>
        {
            Key = kafkaKey,
            Value = payloadJson
        };

        if (headers is not null && headers.Count > 0)
        {
            message.Headers = new Headers();
            foreach (var kvp in headers)
            {
                message.Headers.Add(kvp.Key, Encoding.UTF8.GetBytes(kvp.Value));
            }
        }

        try
        {
            var deliveryResult = await _producer.ProduceAsync(topic, message, cancellationToken);

            if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Đã xuất bản message lên topic {Topic} [P:{Partition}@O:{Offset}] với key {KafkaKey}",
                    deliveryResult.Topic,
                    deliveryResult.Partition.Value,
                    deliveryResult.Offset.Value,
                    kafkaKey);
            }
        }
        catch (ProduceException<string, string> ex)
        {
            _logger?.LogError(
                ex,
                "Lỗi xuất bản Kafka lên topic {Topic} với key {KafkaKey}: {ErrorCode} - {ErrorMessage}",
                topic,
                kafkaKey,
                ex.Error.Code,
                ex.Error.Reason);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProduceEnvelopeAsync<T>(
        string topic,
        MessageEnvelope<T> envelope,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);
        await ProduceRawAsync(topic, envelope.KafkaKey, wireJson, headers, cancellationToken);
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
            _logger?.LogWarning(ex, "Lỗi khi giải phóng KafkaProducer.");
        }
    }
}
