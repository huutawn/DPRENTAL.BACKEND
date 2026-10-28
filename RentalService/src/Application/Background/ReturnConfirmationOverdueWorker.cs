#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Returns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Dprental.RentalService.Application.Background;

public sealed class ReturnConfirmationOverdueWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReturnConfirmationOverdueWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly bool _enabled;

    public ReturnConfirmationOverdueWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReturnConfirmationOverdueWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = configuration.GetValue("Workers:ReturnConfirmationOverdue:Enabled", true);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1,
            configuration.GetValue("Workers:ReturnConfirmationOverdue:PollingIntervalSeconds", 60)));
        _batchSize = Math.Max(1,
            configuration.GetValue("Workers:ReturnConfirmationOverdue:BatchSize", 100));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("ReturnConfirmationOverdueWorker is disabled.");
            return;
        }

        _logger.LogInformation("ReturnConfirmationOverdueWorker started, interval {Interval}s, batchSize {BatchSize}",
            _pollingInterval.TotalSeconds, _batchSize);

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
                _logger.LogWarning(ex, "Error in ReturnConfirmationOverdueWorker loop");
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

        _logger.LogInformation("ReturnConfirmationOverdueWorker stopping");
    }

    public async Task<ReturnConfirmationOverdueBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IReturnConfirmationOverdueService>();
        var result = await service.ProcessBatchAsync(_batchSize, cancellationToken);
        if (result.WarnedUnits > 0)
        {
            _logger.LogInformation("ReturnConfirmationOverdueWorker processed {Orders} orders, flagged {Units} overdue units",
                result.ProcessedOrders, result.WarnedUnits);
        }
        return result;
    }
}
