#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Background;

public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxPublisherWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string? _bootstrapServers;
    private IProducer<string, string>? _producer;

    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OutboxPublisherWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:OutboxPublisher:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        _bootstrapServers = configuration["Kafka:BootstrapServers"];

        if (!string.IsNullOrWhiteSpace(_bootstrapServers))
        {
            try
            {
                var producerConfig = new ProducerConfig
                {
                    BootstrapServers = _bootstrapServers,
                    Acks = Acks.All,
                    EnableIdempotence = true,
                    MessageSendMaxRetries = 3,
                    RetryBackoffMs = 100
                };
                _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể khởi tạo Kafka Producer với BootstrapServers {Servers}", _bootstrapServers);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisherWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

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
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in OutboxPublisherWorker loop");
            }

            try
            {
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxPublisherWorker stopping");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        string workerId = Environment.MachineName + "_" + Environment.ProcessId;

        // Atomically claim batch of pending messages with 30s lease
        var messages = await outboxRepository.ClaimPendingBatchAsync(workerId, TimeSpan.FromSeconds(30), 50, cancellationToken);
        if (messages.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("OutboxPublisherWorker processing {Count} pending messages", messages.Count);

        int publishedCount = 0;
        foreach (var msg in messages)
        {
            try
            {
                if (_producer != null)
                {
                    var deliveryResult = await _producer.ProduceAsync(msg.Topic, new Message<string, string>
                    {
                        Key = msg.PartitionKey,
                        Value = msg.Payload
                    }, cancellationToken);

                    if (deliveryResult.Status != PersistenceStatus.Persisted)
                    {
                        throw new InvalidOperationException($"Kafka broker chưa xác nhận persist message: {deliveryResult.Status}");
                    }
                }
                else
                {
                    // Simulated transport for test/local without broker
                    _logger.LogInformation("Simulated broker ack for outbox message {MessageId} to topic {Topic}", msg.Id, msg.Topic);
                }

                // MarkPublished only AFTER broker acknowledgment
                await outboxRepository.MarkPublishedAsync(msg.Id, workerId, DateTimeOffset.UtcNow, cancellationToken);
                publishedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", msg.Id);

                // Exponential backoff with jitter, capped at 60s
                double baseDelay = Math.Min(60, Math.Pow(2, msg.RetryCount));
                int jitterMs = Random.Shared.Next(0, 1000);
                DateTimeOffset nextRetry = DateTimeOffset.UtcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);
                await outboxRepository.RecordFailureAsync(msg.Id, workerId, ex.Message, DateTimeOffset.UtcNow, nextRetry, cancellationToken);
            }
        }

        return publishedCount;
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}
