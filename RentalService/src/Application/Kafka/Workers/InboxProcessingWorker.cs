#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Workers;

public sealed class InboxProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboxProcessingWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string _dlqTopic;

    [ActivatorUtilitiesConstructor]
    public InboxProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        ILogger<InboxProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var kafkaOptions = options?.Value ?? new KafkaOptions();
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, kafkaOptions.InboxPollingIntervalSeconds));
        _dlqTopic = kafkaOptions.PaymentDlqTopic;
    }

    // Overload for backward-compatibility with tests using IConfiguration
    internal InboxProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InboxProcessingWorker> logger)
        : this(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new KafkaOptions
            {
                InboxPollingIntervalSeconds = configuration.GetValue("Workers:InboxProcessor:PollingIntervalSeconds", 15),
                PaymentDlqTopic = configuration["Kafka:PaymentDlqTopic"] ?? "payment.events.v1.dlq"
            }),
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InboxProcessingWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

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
                _logger.LogWarning(ex, "Error in InboxProcessingWorker loop");
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

        _logger.LogInformation("InboxProcessingWorker stopping");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        IReadOnlyList<InboxMessageDescriptor> descriptors;

        using (var scope = _scopeFactory.CreateScope())
        {
            var inboxRepository = scope.ServiceProvider.GetRequiredService<IInboxRepository>();
            descriptors = await inboxRepository.GetPendingRetryDescriptorsAsync(utcNow, 50, cancellationToken);
        }

        if (descriptors.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("InboxProcessingWorker processing {Count} pending inbox messages", descriptors.Count);

        int processed = 0;
        foreach (var descriptor in descriptors)
        {
            try
            {
                using var messageScope = _scopeFactory.CreateScope();
                var dispatcher = messageScope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                var result = await dispatcher.ProcessInboxMessageAsync(descriptor.Id, cancellationToken);
                if (result.IsSuccess)
                {
                    processed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi xử lý Inbox message {Id}", descriptor.Id);
            }
        }

        return processed;
    }
}
