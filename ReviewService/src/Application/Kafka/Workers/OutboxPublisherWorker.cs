using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Kafka.Workers;

/// <summary>
/// Background worker quét các bản ghi OutboxMessage chưa xuất bản, claim lease độc quyền và publish lên Kafka broker.
/// </summary>
public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaProducer _producer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxPublisherWorker>? _logger;
    private readonly string _workerId = Guid.NewGuid().ToString("N");

    public int BatchSize { get; set; } = 20;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        IKafkaProducer producer,
        TimeProvider? timeProvider = null,
        ILogger<OutboxPublisherWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("OutboxPublisherWorker đã khởi động với worker ID {WorkerId}.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi không xử lý được trong vòng lặp OutboxPublisherWorker.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger?.LogInformation("OutboxPublisherWorker đã dừng.");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var nowUtc = _timeProvider.GetUtcNow();

        var messages = await outboxRepo.ClaimBatchAsync(_workerId, LeaseDuration, BatchSize, nowUtc, cancellationToken);
        if (messages.Count == 0)
        {
            return 0;
        }

        int successCount = 0;
        foreach (var msg in messages)
        {
            try
            {
                bool published = await _producer.ProduceAsync(msg.Topic, msg.PartitionKey, msg.Payload, cancellationToken);
                if (published)
                {
                    await outboxRepo.MarkProcessedAsync(msg.Id, _timeProvider.GetUtcNow(), cancellationToken);
                    successCount++;
                }
                else
                {
                    var nextAttempt = _timeProvider.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, msg.Attempts + 1)));
                    await outboxRepo.FailAttemptAsync(msg.Id, "BROKER_NACK", nextAttempt, false, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi khi publish message {MessageId} lên topic {Topic}", msg.Id, msg.Topic);
                var nextAttempt = _timeProvider.GetUtcNow().AddSeconds(Math.Min(60, Math.Pow(2, msg.Attempts + 1)));
                await outboxRepo.FailAttemptAsync(msg.Id, ex.GetType().Name, nextAttempt, msg.Attempts >= 4, cancellationToken);
            }
        }

        return successCount;
    }
}
