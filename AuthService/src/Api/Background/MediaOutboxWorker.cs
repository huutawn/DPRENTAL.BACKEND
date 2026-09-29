using Dprental.AuthService.Application.IServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.AuthService.Api.Background;

/// <summary>Worker xử lý nền các tác vụ outbox đồng bộ visibility cho MediaService sau khi Auth DB commit.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Acceptable for background worker.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1873:Avoid conditional evaluation", Justification = "Acceptable for background worker.")]
internal sealed class MediaOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MediaOutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MediaOutboxWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                IMediaOutboxService outboxService = scope.ServiceProvider.GetRequiredService<IMediaOutboxService>();
                await outboxService.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during outbox processing round in MediaOutboxWorker.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("MediaOutboxWorker stopping.");
    }
}
