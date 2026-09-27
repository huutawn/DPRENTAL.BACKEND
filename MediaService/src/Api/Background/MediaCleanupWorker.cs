using Dprental.MediaService.Application.IServices;

namespace Dprental.MediaService.Api.Background;

public sealed partial class MediaCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<MediaCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = scopeFactory.CreateScope();
                    var cleanup = scope.ServiceProvider.GetRequiredService<IMediaCleanupService>();
                    await cleanup.SweepExpiredSessionsAsync(cancellationToken: stoppingToken);
                    await cleanup.ProcessPendingCleanupsAsync(cancellationToken: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogCleanupCycleFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Media cleanup cycle failed.")]
    private static partial void LogCleanupCycleFailed(ILogger logger, Exception ex);
}
