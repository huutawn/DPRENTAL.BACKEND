using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.NotificationService.Application.Email.Workers;

public sealed class EmailDeliveryWorker : BackgroundService
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly IEmailDeliveryService? _deliveryService;
    private readonly ILogger<EmailDeliveryWorker> _logger;
    private readonly string _workerId;

    public EmailDeliveryWorker(IServiceProvider serviceProvider, ILogger<EmailDeliveryWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerId = $"worker_{Guid.NewGuid():N}";
    }

    internal EmailDeliveryWorker(IEmailDeliveryService deliveryService, ILogger<EmailDeliveryWorker> logger)
    {
        _deliveryService = deliveryService ?? throw new ArgumentNullException(nameof(deliveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerId = $"worker_{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailDeliveryWorker ({WorkerId}) đã khởi động.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int deliveredCount;
                if (_deliveryService != null)
                {
                    deliveredCount = await _deliveryService.DeliverPendingJobsAsync(_workerId, stoppingToken);
                }
                else
                {
                    using var scope = _serviceProvider!.CreateScope();
                    var deliveryService = scope.ServiceProvider.GetService<IEmailDeliveryService>()
                        ?? new EmailDeliveryService(
                            scope.ServiceProvider.GetRequiredService<IDeliveryJobRepository>(),
                            scope.ServiceProvider.GetRequiredService<IPreferenceRepository>(),
                            scope.ServiceProvider.GetRequiredService<IAuthContactClient>(),
                            scope.ServiceProvider.GetRequiredService<INotificationTemplateRenderer>(),
                            scope.ServiceProvider.GetRequiredService<IEmailSender>(),
                            scope.ServiceProvider.GetService<ILogger<EmailDeliveryService>>() ?? NullLogger<EmailDeliveryService>.Instance);

                    deliveredCount = await deliveryService.DeliverPendingJobsAsync(_workerId, stoppingToken);
                }

                if (deliveredCount == 0)
                {
                    await Task.Delay(3000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp EmailDeliveryWorker. Sẽ thử lại sau 5 giây.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
