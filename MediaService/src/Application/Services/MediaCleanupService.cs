using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.Logging;

namespace Dprental.MediaService.Application.Services;

public partial class MediaCleanupService : IMediaCleanupService
{
    private const int MaxRetries = 3;
    private readonly IMediaCleanupRepository _cleanupRepository;
    private readonly IMediaUploadSessionRepository _sessionRepository;
    private readonly IObjectStorageService _objectStorageService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MediaCleanupService> _logger;

    public MediaCleanupService(
        IMediaCleanupRepository cleanupRepository,
        IMediaUploadSessionRepository sessionRepository,
        IObjectStorageService objectStorageService,
        IUnitOfWork unitOfWork,
        ILogger<MediaCleanupService> logger)
    {
        _cleanupRepository = cleanupRepository ?? throw new ArgumentNullException(nameof(cleanupRepository));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _objectStorageService = objectStorageService ?? throw new ArgumentNullException(nameof(objectStorageService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ProcessPendingCleanupsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        var pendingItems = await _cleanupRepository.GetPendingItemsAsync(limit, cancellationToken);
        if (pendingItems.Count == 0)
        {
            return 0;
        }

        int processedCount = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (var item in pendingItems)
        {
            try
            {
                await _objectStorageService.DeleteObjectAsync(item.StorageKey, cancellationToken);
                item.Status = CleanupItemStatus.Completed;
                item.ProcessedAtUtc = now;
                item.LastError = null;
                processedCount++;
            }
            catch (Exception ex)
            {
                item.RetryCount++;
                item.LastError = ex.Message;
                if (item.RetryCount >= MaxRetries)
                {
                    item.Status = CleanupItemStatus.Failed;
                    item.ProcessedAtUtc = now;
                    LogCleanupFailed(_logger, ex, item.StorageKey, item.RetryCount);
                }
                else
                {
                    LogCleanupRetry(_logger, ex, item.StorageKey, item.RetryCount);
                }
            }

            await _cleanupRepository.UpdateAsync(item, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return processedCount;
    }

    public async Task<int> SweepExpiredSessionsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var expiredSessions = await _sessionRepository.GetExpiredPendingSessionsAsync(now, limit, cancellationToken);
        if (expiredSessions.Count == 0)
        {
            return 0;
        }

        foreach (var session in expiredSessions)
        {
            session.Status = UploadSessionStatus.Expired;
            session.CompletedAtUtc = now;
            await _sessionRepository.UpdateAsync(session, cancellationToken);

            await _cleanupRepository.AddAsync(new MediaCleanupItem
            {
                Id = Guid.NewGuid(),
                StorageKey = session.TemporaryStorageKey,
                Reason = "SessionExpired",
                Status = CleanupItemStatus.Pending,
                CreatedAtUtc = now
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return expiredSessions.Count;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Dọn dẹp object '{StorageKey}' thất bại sau {RetryCount} lần thử.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception ex, string storageKey, int retryCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Thử dọn dẹp object '{StorageKey}' lần thứ {RetryCount} thất bại, sẽ thử lại sau.")]
    private static partial void LogCleanupRetry(ILogger logger, Exception ex, string storageKey, int retryCount);
}
