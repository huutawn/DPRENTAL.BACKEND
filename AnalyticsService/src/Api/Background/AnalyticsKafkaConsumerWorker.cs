using Confluent.Kafka;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Api.Background;

/// <summary>
/// Đọc rental.events.v1 và payment.events.v1. Chỉ lo phần giao tiếp với Kafka (transport):
/// tắt auto commit, chỉ commit offset SAU KHI message đã được lưu DB (hoặc dead letter / bỏ qua).
/// Mọi quyết định xử lý nằm ở <see cref="KafkaRecordProcessor"/> và dispatcher.
/// </summary>
public sealed partial class AnalyticsKafkaConsumerWorker : BackgroundService
{
    private static readonly TimeSpan ConsumeErrorBackoff = TimeSpan.FromSeconds(1);

    private readonly KafkaRecordProcessor _processor;
    private readonly KafkaOptions _options;
    private readonly ILogger<AnalyticsKafkaConsumerWorker> _logger;

    public AnalyticsKafkaConsumerWorker(
        KafkaRecordProcessor processor,
        IOptions<KafkaOptions> options,
        ILogger<AnalyticsKafkaConsumerWorker> logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume() chặn luồng: nhường lại để ứng dụng khởi động xong trước
        await Task.Yield();

        using IConsumer<Ignore, string> consumer = new ConsumerBuilder<Ignore, string>(BuildConfig()).Build();
        consumer.Subscribe([_options.RentalEventsTopic, _options.PaymentEventsTopic]);
        LogStarted(_logger, _options.RentalEventsTopic, _options.PaymentEventsTopic, _options.ConsumerGroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<Ignore, string>? consumed;
                try
                {
                    consumed = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    LogConsumeError(_logger, exception, exception.Error.Code);
                    await Task.Delay(ConsumeErrorBackoff, stoppingToken);
                    continue;
                }

                if (consumed?.Message is null)
                {
                    continue;
                }

                KafkaRecord record = new(
                    consumed.Topic,
                    consumed.Partition.Value,
                    consumed.Offset.Value,
                    consumed.Message.Value ?? string.Empty);

                // Chỉ trả về khi đã lưu xong (hoặc dead letter / bỏ qua); lỗi tạm thời được thử lại bên trong
                await _processor.ProcessAsync(record, stoppingToken);

                try
                {
                    consumer.Commit(consumed);
                }
                catch (KafkaException exception)
                {
                    // Commit lỗi (ví dụ đang chia lại partition): message có thể được giao lại,
                    // inbox / khóa tự nhiên sẽ chặn xử lý trùng.
                    LogCommitError(_logger, exception, record.Topic, record.Partition, record.Offset);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ứng dụng đang tắt
        }
        finally
        {
            consumer.Close(); // Rời consumer group gọn gàng để partition được giao ngay cho instance khác
        }
    }

    private ConsumerConfig BuildConfig() => new()
    {
        BootstrapServers = _options.BootstrapServers,
        GroupId = _options.ConsumerGroupId,
        ClientId = _options.ClientId,
        EnableAutoCommit = false,          // Tự commit sau khi lưu DB
        EnableAutoOffsetStore = false,
        AutoOffsetReset = AutoOffsetReset.Earliest, // Group mới đọc từ đầu để dựng đủ lịch sử báo cáo
        SecurityProtocol = _options.SecurityProtocol,
        SaslMechanism = _options.SaslMechanism,
        SaslUsername = _options.SaslUsername,
        SaslPassword = _options.SaslPassword,
        SslCaLocation = _options.SslCaLocation
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Analytics consumer started on {RentalTopic}, {PaymentTopic} as group {GroupId}")]
    private static partial void LogStarted(ILogger logger, string rentalTopic, string paymentTopic, string groupId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kafka consume error {ErrorCode}")]
    private static partial void LogConsumeError(ILogger logger, Exception exception, ErrorCode errorCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Offset commit failed at {Topic}[{Partition}]@{Offset}; message may be redelivered")]
    private static partial void LogCommitError(ILogger logger, Exception exception, string topic, int partition, long offset);
}
