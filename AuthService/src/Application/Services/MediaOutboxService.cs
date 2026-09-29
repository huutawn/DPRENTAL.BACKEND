using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Acceptable for background service.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1873:Avoid conditional evaluation", Justification = "Acceptable for background service.")]
public sealed class MediaOutboxService(
    IMediaOutboxRepository mediaOutboxRepository,
    IMediaServiceClient mediaServiceClient,
    TimeProvider timeProvider,
    ILogger<MediaOutboxService> logger) : IMediaOutboxService
{
    private const int MaxRetries = 5;

    public async Task QueuePublicVisibilityAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty)
        {
            return;
        }

        var message = new MediaOutboxMessage
        {
            Id = Guid.NewGuid(),
            MediaId = mediaId,
            Visibility = "public",
            Status = "Pending",
            RetryCount = 0,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        await mediaOutboxRepository.AddAsync(message, cancellationToken);
        logger.LogInformation("Queued public visibility outbox task for media {MediaId}", mediaId);
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var messages = await mediaOutboxRepository.GetPendingMessagesAsync(20, cancellationToken);
        if (messages.Count == 0)
        {
            return 0;
        }

        int processedCount = 0;
        foreach (var message in messages)
        {
            try
            {
                var result = await mediaServiceClient.SetVisibilityAsync(message.MediaId, message.Visibility, cancellationToken);
                if (result.IsSuccess)
                {
                    message.Status = "Completed";
                    message.ProcessedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                    message.NextRetryAtUtc = null;
                    message.LastError = null;
                    processedCount++;
                    logger.LogInformation("Successfully set media {MediaId} visibility to {Visibility}",
                        message.MediaId, message.Visibility);
                }
                else
                {
                    message.RetryCount++;
                    message.LastError = result.Error.Code;
                    if (message.RetryCount >= MaxRetries)
                    {
                        message.Status = "Failed";
                        logger.LogError("Media {MediaId} visibility task permanently failed after {Retries} retries: {Error}",
                            message.MediaId, message.RetryCount, message.LastError);
                    }
                    else
                    {
                        message.Status = "Pending";
                        message.NextRetryAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(Math.Pow(2, message.RetryCount));
                        logger.LogWarning("Media {MediaId} visibility task failed (attempt {Retries}): {Error}. Retrying later.",
                            message.MediaId, message.RetryCount, message.LastError);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.GetType().Name;
                if (message.RetryCount >= MaxRetries)
                {
                    message.Status = "Failed";
                    logger.LogError(ex, "Media {MediaId} visibility task permanently failed with exception after {Retries} retries",
                        message.MediaId, message.RetryCount);
                }
                else
                {
                    message.Status = "Pending";
                    message.NextRetryAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(Math.Pow(2, message.RetryCount));
                    logger.LogWarning(ex, "Media {MediaId} visibility task threw exception (attempt {Retries})",
                        message.MediaId, message.RetryCount);
                }
            }

            await mediaOutboxRepository.UpdateAsync(message, cancellationToken);
        }

        return processedCount;
    }
}
