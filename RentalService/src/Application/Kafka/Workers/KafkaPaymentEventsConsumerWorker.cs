#pragma warning disable CA1848, CA1873
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Microsoft.Extensions.Configuration;
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
    private readonly IConsumer<string, string>? _consumer;
    private bool _isSubscribed;

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

        var consumerConfig = new ConsumerConfig
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
            consumerConfig.SecurityProtocol = _options.SecurityProtocol.Value;
        }

        if (_options.SaslMechanism.HasValue)
        {
            consumerConfig.SaslMechanism = _options.SaslMechanism.Value;
        }

        if (!string.IsNullOrWhiteSpace(_options.SaslUsername))
        {
            consumerConfig.SaslUsername = _options.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(_options.SaslPassword))
        {
            consumerConfig.SaslPassword = _options.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslCaLocation))
        {
            consumerConfig.SslCaLocation = _options.SslCaLocation;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslCertificateLocation))
        {
            consumerConfig.SslCertificateLocation = _options.SslCertificateLocation;
        }

        if (!string.IsNullOrWhiteSpace(_options.SslKeyLocation))
        {
            consumerConfig.SslKeyLocation = _options.SslKeyLocation;
        }

        _consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
    }

    // Overload for testing with IConfiguration
    internal KafkaPaymentEventsConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KafkaPaymentEventsConsumerWorker> logger)
        : this(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new KafkaOptions
            {
                Enabled = !string.IsNullOrWhiteSpace(configuration["Kafka:BootstrapServers"]),
                BootstrapServers = configuration["Kafka:BootstrapServers"] ?? string.Empty,
                PaymentEventsTopic = configuration["Kafka:PaymentEventsTopic"] ?? "payment.events.v1",
                ConsumerGroupId = configuration["Kafka:ConsumerGroupId"] ?? "rental-payment-events-consumer"
            }),
            logger)
    {
    }

    // Overload for direct testing with injected IConsumer
    internal KafkaPaymentEventsConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IConsumer<string, string> consumer,
        KafkaOptions options,
        ILogger<KafkaPaymentEventsConsumerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_consumer == null || !_options.Enabled)
        {
            _logger.LogInformation("Kafka Consumer is not active (Kafka disabled); worker exiting ExecuteAsync");
            return;
        }

        _logger.LogInformation("KafkaPaymentEventsConsumerWorker subscribing to {Topic} with GroupId {GroupId}",
            _options.PaymentEventsTopic, _options.ConsumerGroupId);

        if (!_isSubscribed)
        {
            _consumer.Subscribe(_options.PaymentEventsTopic);
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
                await ConsumeNextMessageAsync(stoppingToken);
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
    /// If storage fails, retries the current record and does not commit or advance past it.
    /// </summary>
    public async Task<Result> ConsumeNextMessageAsync(CancellationToken cancellationToken)
    {
        if (_consumer == null || !_options.Enabled)
        {
            return Result.Success();
        }

        if (!_isSubscribed)
        {
            _consumer.Subscribe(_options.PaymentEventsTopic);
            _isSubscribed = true;
        }

        var consumeResult = _consumer.Consume(TimeSpan.FromMilliseconds(_options.ConsumeTimeoutMilliseconds));
        if (consumeResult?.Message == null)
        {
            return Result.Success();
        }

        _logger.LogInformation("Consumed message from {Topic} [P:{Partition}, O:{Offset}] Key={Key}",
            consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, consumeResult.Message.Key);

        Result receiveResult;
        using (var scope = _scopeFactory.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
            receiveResult = await dispatcher.ReceiveMessageAsync(
                consumeResult.Message.Value,
                consumeResult.Topic,
                consumeResult.Partition.Value,
                consumeResult.Offset.Value,
                cancellationToken);
        }

        // If storage fails, retry the held record; DO NOT read or commit past the failing record
        while (receiveResult.IsFailure && receiveResult.Error.Type == ErrorType.Failure && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Lỗi lưu bền vững Kafka payment message [P:{Partition}, O:{Offset}]: {Error}. Thử lại bản ghi hiện tại sau 2s...",
                consumeResult.Partition.Value, consumeResult.Offset.Value, receiveResult.Error.Message);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using var retryScope = _scopeFactory.CreateScope();
            var retryDispatcher = retryScope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
            receiveResult = await retryDispatcher.ReceiveMessageAsync(
                consumeResult.Message.Value,
                consumeResult.Topic,
                consumeResult.Partition.Value,
                consumeResult.Offset.Value,
                cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return receiveResult;
        }

        // Commit offset ONLY after durable Inbox storage / DLQ write or recognized duplicate
        _consumer.Commit(consumeResult);
        _logger.LogInformation("Committed offset {Offset} for partition {Partition}",
            consumeResult.Offset.Value, consumeResult.Partition.Value);

        return receiveResult;
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
