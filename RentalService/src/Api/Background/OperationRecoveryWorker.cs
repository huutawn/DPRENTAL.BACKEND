#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Background;

public sealed class OperationRecoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationRecoveryWorker> _logger;
    private readonly TimeSpan _pollingInterval;

    public OperationRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OperationRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:OperationRecovery:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OperationRecoveryWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

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
                _logger.LogWarning(ex, "Error in OperationRecoveryWorker loop");
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

        _logger.LogInformation("OperationRecoveryWorker stopping");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var receiptRepository = scope.ServiceProvider.GetRequiredService<IOperationReceiptRepository>();
        var executor = scope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>();
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;

        var receipts = await receiptRepository.GetPendingOperationsAsync(utcNow, 50, cancellationToken);
        if (receipts.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("OperationRecoveryWorker recovering {Count} pending operations", receipts.Count);

        int processed = 0;
        foreach (var receipt in receipts)
        {
            try
            {
                var result = await executor.ReplayStoredOperationAsync(receipt.RentalOrderId, receipt.OperationId, cancellationToken);
                if (result.IsSuccess)
                {
                    processed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recover operation {OpId} ({Action})", receipt.OperationId, receipt.Action);
            }
        }

        return processed;
    }
}
