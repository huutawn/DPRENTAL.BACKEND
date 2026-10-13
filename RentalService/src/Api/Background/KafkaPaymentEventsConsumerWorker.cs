#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Background;

public sealed class KafkaPaymentEventsConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaPaymentEventsConsumerWorker> _logger;
    private readonly string? _bootstrapServers;
    private readonly string _topic;
    private readonly string _groupId;
    private IConsumer<string, string>? _consumer;

    public KafkaPaymentEventsConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KafkaPaymentEventsConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _bootstrapServers = configuration["Kafka:BootstrapServers"];
        _topic = configuration["Kafka:PaymentEventsTopic"] ?? "payment.events.v1";
        _groupId = configuration["Kafka:ConsumerGroupId"] ?? "rental-payment-events-consumer";

        if (!string.IsNullOrWhiteSpace(_bootstrapServers))
        {
            try
            {
                var consumerConfig = new ConsumerConfig
                {
                    BootstrapServers = _bootstrapServers,
                    GroupId = _groupId,
                    EnableAutoCommit = false, // Critical: commit offset ONLY after durable inbox storage
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnablePartitionEof = false
                };
                _consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể khởi tạo Kafka Consumer với BootstrapServers {Servers}", _bootstrapServers);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_consumer == null)
        {
            _logger.LogInformation("Kafka Consumer không được khởi tạo (thiếu BootstrapServers); worker ở trạng thái chờ");
            return;
        }

        _logger.LogInformation("KafkaPaymentEventsConsumerWorker subscribing to {Topic} with GroupId {GroupId}", _topic, _groupId);
        _consumer.Subscribe(_topic);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (consumeResult?.Message == null)
                {
                    continue;
                }

                _logger.LogInformation("Consumed message from {Topic} [P:{Partition}, O:{Offset}] Key={Key}",
                    consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, consumeResult.Message.Key);

                // Consumer calls ONLY receive entrypoint: stores Inbox durably or routes to DLQ
                Result receiveResult;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                    receiveResult = await dispatcher.ReceiveMessageAsync(
                        consumeResult.Message.Value,
                        consumeResult.Topic,
                        consumeResult.Partition.Value,
                        consumeResult.Offset.Value,
                        stoppingToken);
                }

                if (receiveResult.IsFailure && receiveResult.Error.Type == ErrorType.Failure)
                {
                    _logger.LogError("Lỗi lưu bền vững Kafka payment message [P:{Partition}, O:{Offset}]: {Error}. Chưa commit offset.",
                        consumeResult.Partition.Value, consumeResult.Offset.Value, receiveResult.Error.Message);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                // Commit offset ONLY after durable Inbox storage / DLQ write or recognized duplicate
                _consumer.Commit(consumeResult);
                _logger.LogInformation("Committed offset {Offset} for partition {Partition}",
                    consumeResult.Offset.Value, consumeResult.Partition.Value);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xử lý Kafka message trong KafkaPaymentEventsConsumerWorker");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger.LogInformation("KafkaPaymentEventsConsumerWorker stopping");
    }

    public override void Dispose()
    {
        try
        {
            _consumer?.Close();
            _consumer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi đóng Kafka consumer");
        }
        base.Dispose();
    }
}
