#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using CommonError = Dprental.RentalService.Common.Error;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Workers;

public sealed class KafkaPaymentEventsConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KafkaPaymentEventsConsumerWorker> _logger;
    private readonly KafkaOptions _options;
    private readonly ConsumerConfig? _consumerConfig;
    private IConsumer<string, string>? _consumer;
    private bool _isSubscribed;
    private readonly HashSet<TopicPartition> _assignedPartitions = new();
    private readonly object _partitionsLock = new();

    [ActivatorUtilitiesConstructor]
    public KafkaPaymentEventsConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        ILogger<KafkaPaymentEventsConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new KafkaOptions();

        if (!_options.Enabled)
        {
            _logger.LogInformation("Kafka is not enabled in configuration; KafkaPaymentEventsConsumerWorker will remain inactive");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BootstrapServers))
        {
            throw new InvalidOperationException("Kafka BootstrapServers must be configured when Kafka is enabled.");
        }

        _consumerConfig = new ConsumerConfig
        {
            BrokerAddressFamily = BrokerAddressFamily.V4,
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            EnableAutoCommit = false, // Critical: commit offset ONLY after durable inbox/dlq storage
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false
        };

        if (_options.SecurityProtocol.HasValue)
        {
            _consumerConfig.SecurityProtocol = _options.SecurityProtocol.Value;
        }

        if (_options.SaslMechanism.HasValue)
        {
            _consumerConfig.SaslMechanism = _options.SaslMechanism.Value;
        }

        if (!string.IsNullOrWhiteSpace(_options.SaslUsername))
        {
            _consumerConfig.SaslUsername = _options.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(_options.SaslPassword))
        {
            _consumerConfig.SaslPassword = _options.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslCaLocation))
        {
            _consumerConfig.SslCaLocation = _options.SslCaLocation;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslCertificateLocation))
        {
            _consumerConfig.SslCertificateLocation = _options.SslCertificateLocation;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslKeyLocation))
        {
            _consumerConfig.SslKeyLocation = _options.SslKeyLocation;
        }

        _consumer = BuildConsumer(_consumerConfig);
    }

    // Overload for direct testing with injected IConsumer
    internal KafkaPaymentEventsConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IConsumer<string, string> consumer,
        IOptions<KafkaOptions> options,
        ILogger<KafkaPaymentEventsConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _options = options?.Value ?? new KafkaOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private IConsumer<string, string> BuildConsumer(ConsumerConfig config)
    {
        return new ConsumerBuilder<string, string>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                lock (_partitionsLock)
                {
                    foreach (var p in partitions)
                    {
                        _assignedPartitions.Add(p);
                    }
                }
                _logger.LogInformation("Partitions assigned: [{Partitions}]", string.Join(", ", partitions));
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                lock (_partitionsLock)
                {
                    foreach (var p in partitions)
                    {
                        _assignedPartitions.Remove(p.TopicPartition);
                    }
                }
                _logger.LogInformation("Partitions revoked: [{Partitions}]", string.Join(", ", partitions.Select(p => p.TopicPartition)));
            })
            .SetPartitionsLostHandler((_, partitions) =>
            {
                lock (_partitionsLock)
                {
                    foreach (var p in partitions)
                    {
                        _assignedPartitions.Remove(p.TopicPartition);
                    }
                }
                _logger.LogWarning("Partitions lost: [{Partitions}]", string.Join(", ", partitions.Select(p => p.TopicPartition)));
            })
            .Build();
    }

    private void RecreateConsumer()
    {
        if (_consumerConfig == null)
        {
            return;
        }

        try
        {
            _consumer?.Close();
            _consumer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing previous consumer during recreation");
        }

        lock (_partitionsLock)
        {
            _assignedPartitions.Clear();
        }

        try
        {
            _consumer = BuildConsumer(_consumerConfig);
            _isSubscribed = false;
            _consumer.Subscribe(new[] { _options.PaymentEventsTopic, _options.InventoryAllocationEventsTopic });
            _isSubscribed = true;
            _logger.LogInformation("Recreated Kafka consumer successfully; resuming from committed offset");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recreate Kafka consumer");
        }
    }

    private bool IsPartitionOwned(TopicPartition tp)
    {
        lock (_partitionsLock)
        {
            if (_assignedPartitions.Count > 0)
            {
                return _assignedPartitions.Contains(tp);
            }
        }

        try
        {
            var assignment = _consumer?.Assignment;
            return assignment != null && assignment.Contains(tp);
        }
        catch
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_consumer == null || !_options.Enabled)
        {
            _logger.LogInformation("Kafka Consumer is not active (Kafka disabled); worker exiting ExecuteAsync");
            return;
        }

        var topics = new[] { _options.PaymentEventsTopic, _options.InventoryAllocationEventsTopic };
        _logger.LogInformation("KafkaPaymentEventsConsumerWorker subscribing to [{Topics}] with GroupId {GroupId}",
            string.Join(", ", topics), _options.ConsumerGroupId);

        if (!_isSubscribed)
        {
            _consumer.Subscribe(topics);
            _isSubscribed = true;
        }

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
                var result = await ConsumeNextMessageAsync(stoppingToken);
                if (result.IsFailure && !stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xử lý Kafka message trong KafkaPaymentEventsConsumerWorker");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("KafkaPaymentEventsConsumerWorker stopping");
    }

    /// <summary>
    /// Consumes the next message from the broker, durably stores to Inbox or DLQ, and commits offset only on success.
    /// If storage fails, seeks back to the failing record's offset and retries without advancing past it.
    /// </summary>
    public async Task<Result> ConsumeNextMessageAsync(CancellationToken cancellationToken)
    {
        if (_consumer == null || !_options.Enabled)
        {
            return Result.Success();
        }

        if (!_isSubscribed)
        {
            _consumer.Subscribe(new[] { _options.PaymentEventsTopic, _options.InventoryAllocationEventsTopic });
            _isSubscribed = true;
        }

        ConsumeResult<string, string>? consumeResult;
        try
        {
            consumeResult = _consumer.Consume(TimeSpan.FromMilliseconds(_options.ConsumeTimeoutMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(CommonError.Failure("CANCELLED", "Consumer stopped."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while consuming from Kafka topic {Topic}", _options.PaymentEventsTopic);
            return Result.Failure(CommonError.Failure("KAFKA_CONSUME_ERROR", ex.Message));
        }

        if (consumeResult?.Message == null)
        {
            return Result.Success();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(CommonError.Failure("CANCELLED", "Consumer stopped."));
        }

        _logger.LogInformation("Consumed message from {Topic} [P:{Partition}, O:{Offset}] Key={Key}",
            consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, consumeResult.Message.Key);

        if (!IsPartitionOwned(consumeResult.TopicPartition))
        {
            _logger.LogWarning("Partition {TopicPartition} is not owned; skipping message without commit", consumeResult.TopicPartition);
            return Result.Success();
        }

        using var intakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        intakeCts.CancelAfter(TimeSpan.FromSeconds(10));

        Result<KafkaIntakeDisposition> receiveResult;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
            receiveResult = await dispatcher.ReceiveMessageAsync(
                consumeResult.Message.Value,
                consumeResult.Topic,
                consumeResult.Partition.Value,
                consumeResult.Offset.Value,
                intakeCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(CommonError.Failure("CANCELLED", "Consumer stopped during cancellation."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi intake Kafka payment message [P:{Partition}, O:{Offset}]",
                consumeResult.Partition.Value, consumeResult.Offset.Value);
            receiveResult = CommonError.Failure("INTAKE_ERROR", ex.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(CommonError.Failure("CANCELLED", "Consumer stopped."));
        }
        if (receiveResult.IsSuccess)
        {
            if (IsPartitionOwned(consumeResult.TopicPartition))
            {
                try
                {
                    _consumer.Commit(consumeResult);
                    _logger.LogInformation("Committed offset {Offset} for partition {Partition} (Disposition={Disposition})",
                        consumeResult.Offset.Value, consumeResult.Partition.Value, receiveResult.Value);
                    return Result.Success();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit offset {Offset} for partition {Partition}",
                        consumeResult.Offset.Value, consumeResult.Partition.Value);
                    RecreateConsumer();
                    return CommonError.Failure("COMMIT_FAILED", ex.Message);
                }
            }
            else
            {
                _logger.LogWarning("Partition {TopicPartition} lost before commit; closing and recreating consumer", consumeResult.TopicPartition);
                RecreateConsumer();
                return CommonError.Failure("PARTITION_LOST", "Partition ownership lost before offset commit.");
            }
        }
        else
        {
            _logger.LogWarning("Intake failed for [P:{Partition}, O:{Offset}]: {Error}. Seeking back to offset for retry.",
                consumeResult.Partition.Value, consumeResult.Offset.Value, receiveResult.Error.Message);

            try
            {
                if (IsPartitionOwned(consumeResult.TopicPartition))
                {
                    _consumer.Seek(consumeResult.TopicPartitionOffset);
                }
                else
                {
                    _logger.LogWarning("Partition {TopicPartition} lost; cannot seek, recreating consumer", consumeResult.TopicPartition);
                    RecreateConsumer();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seek failed on {TopicPartitionOffset}. Recreating consumer to resume from committed offset.", consumeResult.TopicPartitionOffset);
                RecreateConsumer();
            }

            return Result.Failure(receiveResult.Error);
        }
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
