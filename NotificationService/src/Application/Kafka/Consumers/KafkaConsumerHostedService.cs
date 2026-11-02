using Confluent.Kafka;
using Dprental.NotificationService.Application.Kafka.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Kafka.Consumers;

public sealed class KafkaConsumerHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KafkaConsumerHostedService> _logger;

    public KafkaConsumerHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<KafkaConsumerHostedService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogInformation("Kafka:BootstrapServers chưa được cấu hình. Kafka consumer background service sẽ ở trạng thái chờ.");
            return;
        }

        var topic = _configuration["Kafka:EventsTopic"] ?? "rental.events.v1";
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

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        _logger.LogInformation("Kafka consumer đã kết nối tới {Servers}, lắng nghe topic '{Topic}', group '{GroupId}'.", bootstrapServers, topic, groupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                if (consumeResult == null || consumeResult.IsPartitionEOF)
                {
                    continue;
                }

                using var scope = _serviceProvider.CreateScope();
                var ingestionService = scope.ServiceProvider.GetRequiredService<INotificationIngestionService>();

                var outcome = await ingestionService.IngestMessageAsync(
                    consumeResult.Message.Value,
                    consumeResult.Topic,
                    consumeResult.Partition.Value,
                    consumeResult.Offset.Value,
                    consumeResult.Message.Key,
                    stoppingToken);

                // Commit offset sau khi xử lý thành công hoặc đã đẩy DLQ
                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp tiêu thụ Kafka message. Sẽ thử lại sau 2 giây.");
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
