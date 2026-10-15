#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Overdue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dprental.RentalService.Application.Options;

namespace Dprental.RentalService.Application.Background;

public sealed class OverdueProtectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OverdueProtectionWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;

    public OverdueProtectionWorker(
        IServiceScopeFactory scopeFactory, IOptions<OverdueProtectionOptions> options,
        ILogger<OverdueProtectionWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        OverdueProtectionOptions configured = options.Value;
        _enabled = configured.Enabled;
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, configured.ScanIntervalSeconds));
        _batchSize = Math.Max(1, configured.BatchSize);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) { _logger.LogInformation("OverdueProtectionWorker đang tắt."); return; }
        _logger.LogInformation("OverdueProtectionWorker started, interval {Interval}s", _pollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Error in OverdueProtectionWorker loop"); }

            try { await Task.Delay(_pollingInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("OverdueProtectionWorker stopping");
    }

    public async Task<OverdueProtectionBatchResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> orderIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
            orderIds = await service.GetDueOrderIdsAsync(_batchSize, cancellationToken);
        }

        if (orderIds.Count == 0) return new(0, 0, 0, 0, 0);

        int scheduled = 0, succeeded = 0, failed = 0, skipped = 0;
        foreach (Guid orderId in orderIds)
        {
            try
            {
                using var orderScope = _scopeFactory.CreateScope();   // DbContext mới cho mỗi order
                var service = orderScope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
                var r = await service.ProcessOrderAsync(orderId, cancellationToken);

                if (r.Scheduled == 0) { skipped++; continue; }
                scheduled += r.Scheduled; succeeded += r.Succeeded; failed += r.Failed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overdue protection lỗi cho order {OrderId}", orderId);
                failed++;
            }
        }

        _logger.LogInformation(
            "Overdue scan: orders={Orders} scheduled={Scheduled} succeeded={Ok} failed={Failed} skipped={Skipped}",
            orderIds.Count, scheduled, succeeded, failed, skipped);

        return new(orderIds.Count, scheduled, succeeded, failed, skipped);
    }
}
