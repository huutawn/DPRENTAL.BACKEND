using Confluent.Kafka;
using Dprental.NotificationService.Application.Kafka.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Kafka.Consumers;

public sealed class KafkaConsumerHostedService : BackgroundService
{
    private const int MaxTransientRetries = 3;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KafkaConsumerHostedService> _logger;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private readonly Func<ConsumerConfig, IConsumer<string, string>> _consumerFactory;

    public KafkaConsumerHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<KafkaConsumerHostedService> logger,
        IHostApplicationLifetime? hostApplicationLifetime = null,
        Func<ConsumerConfig, IConsumer<string, string>>? consumerFactory = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostApplicationLifetime = hostApplicationLifetime;
        _consumerFactory = consumerFactory ?? (config => new ConsumerBuilder<string, string>(config).Build());
    }

    public static IReadOnlyList<string> ResolveConfiguredTopics(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var topicsSection = configuration.GetSection("Kafka:Topics");
        var topicList = topicsSection.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        if (topicList.Count > 0)
        {
            return topicList!;
        }

        var topicsValue = configuration["Kafka:Topics"];
        if (!string.IsNullOrWhiteSpace(topicsValue))
        {
            var parsed = topicsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        return new[] { "rental.events.v1", "payment.analytics.v1" };
    }
    public Task ExecuteConsumerAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogInformation("Kafka:BootstrapServers chưa được cấu hình. Kafka consumer background service sẽ ở trạng thái chờ.");
            return;
        }

        var topics = ResolveConfiguredTopics(_configuration);
        var groupId = _configuration["Kafka:GroupId"] ?? "notification-service-v1";

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false
        };

        using var consumer = _consumerFactory(config);
        consumer.Subscribe(topics);
        _logger.LogInformation("Kafka consumer đã kết nối tới {Servers}, lắng nghe topics [{Topics}], group '{GroupId}'.",
            bootstrapServers, string.Join(", ", topics), groupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? consumeResult = null;
            try
            {
                consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                if (consumeResult == null || consumeResult.IsPartitionEOF)
                {
                    continue;
                }

                var success = false;
                for (var attempt = 1; attempt <= MaxTransientRetries && !stoppingToken.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var ingestionService = scope.ServiceProvider.GetRequiredService<INotificationIngestionService>();

                        var outcome = await ingestionService.IngestMessageAsync(
                            consumeResult.Message.Value,
                            consumeResult.Topic,
                            consumeResult.Partition.Value,
                            consumeResult.Offset.Value,
                            consumeResult.Message.Key,
                            stoppingToken);

                        // Commit offset sau khi xử lý thành công, duplicate, bỏ qua hoặc đã đưa vào DLQ thành công
                        consumer.Commit(consumeResult);
                        success = true;
                        break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi xử lý Kafka message [Topic={Topic}, Partition={Partition}, Offset={Offset}]. Lần thử {Attempt}/{Max}",
                            consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, attempt, MaxTransientRetries);

                        if (attempt < MaxTransientRetries)
                        {
                            var delayMs = (int)(500 * Math.Pow(2, attempt - 1));
                            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), stoppingToken);
                        }
                    }
                }

                if (!success && !stoppingToken.IsCancellationRequested)
                {
                    // Lỗi hạ tầng sau 3 lần thử: Dừng consumer và host, KHÔNG commit offset
                    _logger.LogCritical("Lỗi hạ tầng xử lý Kafka message tại [Topic={Topic}, Partition={Partition}, Offset={Offset}] sau {Max} lần thử. Dừng consumer và host không commit offset.",
                        consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, MaxTransientRetries);

                    _hostApplicationLifetime?.StopApplication();
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp tiêu thụ Kafka message.");
                await Task.Delay(2000, stoppingToken);
            }
        }

        try
        {
            consumer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi khi đóng Kafka consumer.");
        }
    }
}
