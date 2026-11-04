using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface INotificationRepository
{
    Task<Notification?> GetByIdAndUserAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Notification> Items, int TotalCount)> GetUserNotificationsPagedAsync(Guid userId, string? category, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> MarkAsReadAsync(Guid id, Guid userId, DateTimeOffset readAt, CancellationToken cancellationToken = default);
    Task<int> MarkAllAsReadAsync(Guid userId, DateTimeOffset cutoffUtc, DateTimeOffset readAt, CancellationToken cancellationToken = default);
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<Notification> notifications, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Notification>> SuppressPendingActionsForResourceAsync(string resourceType, string resourceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Notification>> SuppressPendingActionsForOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Notification>> GetByRentalItemIdAsync(Guid rentalItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Notification>> DismissReturnOverdueActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Notification>> SuppressPendingActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default);
}
