using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.IRepositories;

namespace Dprental.NotificationService.Application.Services;

public sealed class NotificationQueryService : INotificationQueryService
{
    private readonly INotificationRepository _notificationRepo;

    public NotificationQueryService(INotificationRepository notificationRepo)
    {
        _notificationRepo = notificationRepo ?? throw new ArgumentNullException(nameof(notificationRepo));
    }

    public async Task<Result<NotificationListResponseDto>> GetNotificationsAsync(
        Guid userId,
        string? category,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            return Result.Failure<NotificationListResponseDto>(
                Error.Validation("INVALID_PAGINATION", "Tham số 'page' phải lớn hơn hoặc bằng 1."));
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Result.Failure<NotificationListResponseDto>(
                Error.Validation("INVALID_PAGINATION", "Tham số 'pageSize' phải nằm trong khoảng từ 1 đến 100."));
        }

        if (!string.IsNullOrWhiteSpace(category) && !PreferencePolicy.IsValidCategory(category))
        {
            return Result.Failure<NotificationListResponseDto>(
                Error.Validation("INVALID_CATEGORY", $"Category '{category}' không hợp lệ. Phải là một trong: ORDER, PAYMENT, HANDOVER, RETURN."));
        }

        var unreadOnly = string.Equals(status, "unread", StringComparison.OrdinalIgnoreCase);

        var (items, totalCount) = await _notificationRepo.GetUserNotificationsPagedAsync(
            userId,
            category?.ToUpperInvariant(),
            unreadOnly,
            page,
            pageSize,
            cancellationToken);

        var dtos = items.Select(n => new NotificationListItemDto(
            Id: n.Id,
            NotificationType: n.NotificationType,
            Category: n.Category,
            Severity: n.Severity,
            Title: n.Title,
            Body: n.Body,
            ResourceType: n.ResourceType,
            ResourceId: n.ResourceId,
            DeepLinkUrl: n.DeepLinkUrl,
            IsActionable: n.IsActionable,
            ActionUrl: n.ActionUrl,
            IsRead: n.ReadAt.HasValue,
            ReadAt: n.ReadAt,
            OccurredAt: n.OccurredAt,
            CreatedAt: n.CreatedAt)).ToList();

        var totalPages = totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;
        var hasPrevious = page > 1;
        var hasNext = page < totalPages;

        return Result.Success(new NotificationListResponseDto(
            Items: dtos,
            Page: page,
            PageSize: pageSize,
            TotalCount: totalCount,
            TotalPages: totalPages,
            HasPrevious: hasPrevious,
            HasNext: hasNext));
    }

    public async Task<Result<UnreadCountResponseDto>> GetUnreadCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var count = await _notificationRepo.GetUnreadCountAsync(userId, cancellationToken);
        return Result.Success(new UnreadCountResponseDto(count));
    }
}
