#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Background;

public sealed class InboxProcessingWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboxProcessingWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string _dlqTopic;

    public InboxProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InboxProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:InboxProcessor:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        _dlqTopic = configuration["Kafka:PaymentDlqTopic"] ?? "payment.events.v1.dlq";
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
