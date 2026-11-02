using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.IServices;

public interface INotificationQueryService
{
    Task<Result<NotificationListResponseDto>> GetNotificationsAsync(
        Guid userId,
        string? category,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<UnreadCountResponseDto>> GetUnreadCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public interface INotificationReadService
{
    Task<Result> MarkAsReadAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Result<MarkAllReadResponseDto>> MarkAllAsReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public interface IPreferenceService
{
    Task<Result<PreferencesResponseDto>> GetPreferencesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Result<PreferencesResponseDto>> UpdatePreferencesAsync(
        Guid userId,
        UpdatePreferencesRequestDto request,
        CancellationToken cancellationToken = default);
}
