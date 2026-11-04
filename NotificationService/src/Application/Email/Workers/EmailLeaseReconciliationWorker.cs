using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dprental.NotificationService.Application.Diagnostics;

namespace Dprental.NotificationService.Application.Email.Workers;

public sealed class EmailLeaseReconciliationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailLeaseReconciliationWorker> _logger;

    public EmailLeaseReconciliationWorker(
        IServiceProvider serviceProvider,
        ILogger<EmailLeaseReconciliationWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailLeaseReconciliationWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var deliveryRepo = scope.ServiceProvider.GetRequiredService<IDeliveryJobRepository>();

                var recoveredCount = await deliveryRepo.RecoverExpiredLeasesAsync(stoppingToken);
                if (recoveredCount > 0)
                {
                    _logger.LogInformation("EmailLeaseReconciliationWorker đã phục hồi {Count} job bị hết hạn lease.", recoveredCount);
                    NotificationMetrics.LeaseRecoveryCounter.Add(recoveredCount);
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp EmailLeaseReconciliationWorker.");
                await Task.Delay(10000, stoppingToken);
            }
        }
    }
}
