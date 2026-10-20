#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Workers;

public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxPublisherWorker> _logger;
    private readonly KafkaOptions _options;
    private readonly IKafkaProducerTransport? _transport;

    [ActivatorUtilitiesConstructor]
    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        ILogger<OutboxPublisherWorker> logger,
        IKafkaProducerTransport? transport = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new KafkaOptions();
        _transport = transport;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Kafka is disabled; OutboxPublisherWorker will remain inactive");
            return;
        }

        var pollingInterval = TimeSpan.FromSeconds(Math.Max(1, _options.OutboxPollingIntervalSeconds));
        _logger.LogInformation("OutboxPublisherWorker started with polling interval {Interval}s", pollingInterval.TotalSeconds);

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
                await Task.Delay(pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxPublisherWorker stopping");
    }

    /// <summary>
    /// Processes up to MaxBatchPerRound (default 50) messages per round by claiming one message
    /// at a time immediately before publishing, ensuring leases do not expire during the batch.
    /// When Kafka is disabled, does not process backlog, claim messages, increment retries or touch Outbox.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        int maxMessages = Math.Max(1, _options.MaxBatchPerRound);
        int publishedCount = 0;
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(5, _options.LeaseDurationSeconds));

        for (int i = 0; i < maxMessages; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using var scope = _scopeFactory.CreateScope();
            var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            string claimToken = Guid.NewGuid().ToString("N");

            // Claim exactly ONE message immediately before sending
            var msg = await outboxRepository.ClaimNextPendingAsync(claimToken, leaseDuration, cancellationToken);
            if (msg == null)
            {
                // No more pending messages available
                break;
            }

            try
            {
                // Pre-publish lease check: verify lease is still valid
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                if (msg.LockedUntilUtc == null || msg.LockedUntilUtc <= nowUtc)
                {
                    _logger.LogWarning("Outbox message {MessageId} lease expired before publish could start.", msg.Id);
                    continue;
                }

                if (_transport == null)
                {
                    throw new InvalidOperationException("Kafka producer transport chưa được cấu hình hoặc không kết nối được broker.");
                }

                using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                publishCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.DeliveryTimeoutSeconds)));

                var deliveryResult = await _transport.ProduceAsync(
                    msg.Topic,
                    msg.PartitionKey,
                    msg.Payload,
                    publishCts.Token);

                if (deliveryResult.Status != PersistenceStatus.Persisted)
                {
                    throw new InvalidOperationException($"Kafka broker chưa xác nhận persist message: {deliveryResult.Status}");
                }

                // MarkPublished only AFTER broker acknowledgment, returns true only if lease was still valid
                bool published = await outboxRepository.MarkPublishedAsync(msg.Id, claimToken, DateTimeOffset.UtcNow, cancellationToken);
                if (published)
                {
                    publishedCount++;
                }
                else
                {
                    _logger.LogWarning("Outbox message {MessageId} lease expired or was reclaimed before completion could be saved.", msg.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Outbox publisher process cancelled during host shutdown.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", msg.Id);

                string targetStatus;
                if (IsPermanentError(ex))
                {
                    targetStatus = "DeadLettered";
                }
                else if (IsTransientError(ex))
                {
                    targetStatus = "Pending";
                }
                else
                {
                    targetStatus = msg.RetryCount + 1 >= 10 ? "DeadLettered" : "Pending";
                }

                int safeExponent = Math.Clamp(msg.RetryCount, 0, 6);
                double baseDelay = Math.Min(60, Math.Pow(2, safeExponent));
                int jitterMs = Random.Shared.Next(0, 1000);
                DateTimeOffset? nextRetry = targetStatus == "DeadLettered"
                    ? null
                    : DateTimeOffset.UtcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

                await outboxRepository.RecordFailureAsync(
                    msg.Id,
                    claimToken,
                    ex.Message,
                    DateTimeOffset.UtcNow,
                    nextRetry,
                    targetStatus,
                    CancellationToken.None);
            }
        }

        return publishedCount;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OutboxPublisherWorker StopAsync initiated");

        try
        {
            var shutdownTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.ShutdownTimeoutSeconds));
            _transport?.Flush(shutdownTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing producer transport during shutdown");
        }

        await base.StopAsync(cancellationToken);
    }

    private static bool IsTransientError(Exception ex)
    {
        return ex is Confluent.Kafka.KafkaException kEx && !kEx.Error.IsFatal && !kEx.Error.IsLocalError ||
               ex is TimeoutException ||
               ex is System.Net.Sockets.SocketException ||
               ex is System.IO.IOException ||
               ex.Message.Contains("broker", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("chưa xác nhận persist", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("chưa được cấu hình", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermanentError(Exception ex)
    {
        return ex is System.Text.Json.JsonException ||
               ex is InvalidDataException ||
               ex is FormatException ||
               (ex is Confluent.Kafka.KafkaException kEx && (kEx.Error.Code == Confluent.Kafka.ErrorCode.TopicException || kEx.Error.Code == Confluent.Kafka.ErrorCode.TopicAuthorizationFailed || kEx.Error.Code == Confluent.Kafka.ErrorCode.GroupAuthorizationFailed));
    }
}
