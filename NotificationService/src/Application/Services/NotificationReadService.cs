using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.NotificationService.Application.Services;

public sealed class NotificationReadService : INotificationReadService
{
    private readonly INotificationRepository _notificationRepo;
    private readonly INotificationRealtimeNotifier _realtimeNotifier;
    private readonly TimeProvider _timeProvider;

    [ActivatorUtilitiesConstructor]
    public NotificationReadService(
        INotificationRepository notificationRepo,
        INotificationRealtimeNotifier realtimeNotifier,
        TimeProvider? timeProvider = null)
    {
        _notificationRepo = notificationRepo ?? throw new ArgumentNullException(nameof(notificationRepo));
        _realtimeNotifier = realtimeNotifier ?? throw new ArgumentNullException(nameof(realtimeNotifier));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }


    public async Task<Result> MarkAsReadAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _notificationRepo.GetByIdAndUserAsync(notificationId, userId, cancellationToken);
        if (existing == null)
        {
            return Result.Failure(
                Error.NotFound("NOTIFICATION_NOT_FOUND", $"Không tìm thấy thông báo '{notificationId}' hoặc người dùng không có quyền sở hữu."));
        }

        var readAt = _timeProvider.GetUtcNow();
        var wasUpdated = await _notificationRepo.MarkAsReadAsync(notificationId, userId, readAt, cancellationToken);
        if (wasUpdated)
        {
            await _realtimeNotifier.NotifyInboxChangedAsync(userId, notificationId, "READ", existing.Category, readAt, cancellationToken);
        }
        return Result.Success();
    }

    public async Task<Result<MarkAllReadResponseDto>> MarkAllAsReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Server xác định thời điểm cutoff và readAt từ authoritative clock
        var cutoffUtc = _timeProvider.GetUtcNow();
        var readAt = cutoffUtc;

        var markedCount = await _notificationRepo.MarkAllAsReadAsync(userId, cutoffUtc, readAt, cancellationToken);

        if (markedCount > 0)
        {
            await _realtimeNotifier.NotifyInboxChangedAsync(userId, null, "READ_ALL", "ALL", readAt, cancellationToken);
        }
        return Result.Success(new MarkAllReadResponseDto(
            MarkedCount: markedCount,
            CutoffUtc: cutoffUtc,
            ReadAt: readAt));
    }
}
