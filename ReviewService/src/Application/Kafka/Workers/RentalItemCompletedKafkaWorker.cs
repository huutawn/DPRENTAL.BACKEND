using Confluent.Kafka;
using Dprental.ReviewService.Application.Kafka.Consumers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Kafka.Workers;

/// <summary>
/// Hosted BackgroundService làm transport adapter cho Kafka consumer.
/// Duy trì vòng lặp consume tuần tự theo partition, commit offset thủ công sau khi xử lý thành công hoặc DLQ ACK.
/// Tuyệt đối không chứa DbContext, logic eligibility hay DLQ format.
/// </summary>
public sealed class RentalItemCompletedKafkaWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RentalItemCompletedKafkaWorker>? _logger;
    private readonly Func<ConsumerConfig, IConsumer<string, string>>? _consumerFactory;

    public RentalItemCompletedKafkaWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<RentalItemCompletedKafkaWorker>? logger = null,
        Func<ConsumerConfig, IConsumer<string, string>>? consumerFactory = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger;
        _consumerFactory = consumerFactory;
    }

    public Task ExecuteWorkerAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger?.LogWarning("Kafka:BootstrapServers chưa được cấu hình. RentalItemCompletedKafkaWorker sẽ không khởi động.");
            return;
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = RentalItemCompletedConsumer.ConsumerGroup,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollIntervalMs = 300000
        };

        using var consumer = _consumerFactory != null
            ? _consumerFactory(config)
            : new ConsumerBuilder<string, string>(config).Build();

        consumer.Subscribe(RentalItemCompletedConsumer.Topic);
        _logger?.LogInformation("RentalItemCompletedKafkaWorker đã đăng ký topic {Topic} với consumer group {Group}.",
            RentalItemCompletedConsumer.Topic, RentalItemCompletedConsumer.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? consumeResult = null;
                try
                {
                    consumeResult = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex)
                {
                    _logger?.LogWarning(ex, "Lỗi ConsumeException trên topic {Topic}.", RentalItemCompletedConsumer.Topic);
                    continue;
                }

                if (consumeResult == null || consumeResult.IsPartitionEOF)
                {
                    continue;
                }

                // Giới hạn thời gian một lần xử lý dưới 60 giây (55 giây timeout)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(55));

                ProcessOutcome outcome = ProcessOutcome.Retry;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var consumerHandler = scope.ServiceProvider.GetRequiredService<IRentalItemCompletedConsumer>();

                    var messageContext = new KafkaMessageContext(
                        consumeResult.Topic,
                        consumeResult.Partition.Value,
                        consumeResult.Offset.Value,
                        consumeResult.Message.Key,
                        consumeResult.Message.Value);

                    outcome = await consumerHandler.ProcessMessageAsync(messageContext, cts.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Đang shutdown ứng dụng: tuyệt đối không commit record chưa hoàn tất
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ngoại lệ chưa bắt khi xử lý bản tin tại offset {Offset}.", consumeResult.Offset);
                    outcome = ProcessOutcome.Retry;
                }

                // Worker chỉ commit offset cụ thể của record sau ba outcome: Handled, Ignored, DeadLettered
                if (outcome is ProcessOutcome.Handled or ProcessOutcome.Ignored or ProcessOutcome.DeadLettered)
                {
                    try
                    {
                        consumer.Commit(consumeResult);
                    }
                    catch (KafkaException ex)
                    {
                        _logger?.LogWarning(ex, "Không thể commit offset {Offset} (ví dụ mất quyền sở hữu partition do rebalance).", consumeResult.Offset);
                        // Khi rebalance hoặc commit thất bại vì mất partition ownership: không coi là đã commit;
                        // để message được phát lại và dựa vào transactional inbox deduplication.
                    }
                }
                else if (outcome == ProcessOutcome.Retry)
                {
                    _logger?.LogWarning("Bản tin tại offset {Offset} cần Retry. Tạm dừng 5 giây và lùi offset về bản tin này...",
                        consumeResult.Offset);
                    try
                    {
                        consumer.Seek(consumeResult.TopicPartitionOffset);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Không thể seek lùi về offset {Offset}.", consumeResult.Offset);
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Ngoại lệ khi đóng Kafka consumer.");
            }
        }
    }
}
