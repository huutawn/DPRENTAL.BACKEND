#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Settlement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Background;

public sealed class DeductionDeadlineWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeductionDeadlineWorker> _logger;
    private readonly TimeSpan _pollingInterval;

    public DeductionDeadlineWorker(
        IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DeductionDeadlineWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1,
            configuration.GetValue("Workers:DeductionDeadline:PollingIntervalSeconds", 30)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeductionDeadlineWorker started, interval {Interval}s", _pollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessTimeoutsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in DeductionDeadlineWorker loop"); }

            try { await Task.Delay(_pollingInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("DeductionDeadlineWorker stopping");
    }

    public async Task<int> ProcessTimeoutsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeductionService>();
        int count = await service.ProcessDeductionTimeoutsAsync(cancellationToken);
        var responses = scope.ServiceProvider.GetRequiredService<IDeductionResponseService>();
        count += await responses.ProcessRenterResponseTimeoutsAsync(cancellationToken);
        count += await service.ScheduleReadySettlementsAsync(cancellationToken);
        if (count > 0) _logger.LogInformation("DeductionDeadlineWorker processed {Count} deadlines and settlement schedules", count);
        return count;
    }
}
