#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Expiry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Background;

public sealed class ExpiryScannerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiryScannerWorker> _logger;
    private readonly TimeSpan _pollingInterval;

    public ExpiryScannerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ExpiryScannerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:ExpiryScanner:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExpiryScannerWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

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
                await ProcessTimeoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in ExpiryScannerWorker loop");
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

        _logger.LogInformation("ExpiryScannerWorker stopping");
    }

    public async Task ProcessTimeoutsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var expiryService = scope.ServiceProvider.GetRequiredService<IExpiryWorkflowService>();

        int expiredPayments = await expiryService.ProcessPaymentTimeoutsAsync(cancellationToken);
        if (expiredPayments > 0)
        {
            _logger.LogInformation("ExpiryScannerWorker processed {Count} payment timeouts", expiredPayments);
        }

        int expiredSuppliers = await expiryService.ProcessSupplierTimeoutsAsync(cancellationToken);
        if (expiredSuppliers > 0)
        {
            _logger.LogInformation("ExpiryScannerWorker processed {Count} supplier timeouts", expiredSuppliers);
        }
    }
}
