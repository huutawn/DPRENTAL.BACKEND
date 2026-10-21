using Dprental.RecommendationService.Application.IServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RecommendationService.Application.Background;

public sealed class ValkeySnapshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ValkeyOptions _options;
    private readonly ILogger<ValkeySnapshotWorker> _logger;

    public ValkeySnapshotWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ValkeyOptions> options,
        ILogger<ValkeySnapshotWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("ValkeySnapshotWorker is disabled in configuration.");
            return;
        }

        _logger.LogInformation("Starting ValkeySnapshotWorker (Interval: {Interval}s).", _options.IntervalSeconds);

        // Run immediately on startup
        await RunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCycleAsync(stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var publishingService = scope.ServiceProvider.GetRequiredService<ISnapshotPublishingService>();
            await publishingService.RebuildCacheAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in Valkey snapshot worker cycle. Non-fatal for service.");
        }
    }
}
