using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public NotificationRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Notification?> GetByIdAndUserAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id && n.RecipientUserId == userId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Notification> Items, int TotalCount)> GetUserNotificationsPagedAsync(
        Guid userId,
        string? category,
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == userId);

        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(n => n.Category == category);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == userId && n.ReadAt == null)
            .CountAsync(cancellationToken);
    }

    public async Task<bool> MarkAsReadAsync(Guid id, Guid userId, DateTimeOffset readAt, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            var updated = await _dbContext.Notifications
                .Where(n => n.Id == id && n.RecipientUserId == userId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, readAt), cancellationToken);

            return updated > 0;
        }
        else
        {
            var entity = await _dbContext.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.RecipientUserId == userId, cancellationToken);

            if (entity == null || entity.ReadAt != null)
            {
                return false;
            }

            entity.ReadAt = readAt;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<int> MarkAllAsReadAsync(Guid userId, DateTimeOffset cutoffUtc, DateTimeOffset readAt, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            return await _dbContext.Notifications
                .Where(n => n.RecipientUserId == userId && n.ReadAt == null && n.CreatedAt <= cutoffUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, readAt), cancellationToken);
        }
        else
        {
            var entities = await _dbContext.Notifications
                .Where(n => n.RecipientUserId == userId && n.ReadAt == null && n.CreatedAt <= cutoffUtc)
                .ToListAsync(cancellationToken);

            foreach (var entity in entities)
            {
                entity.ReadAt = readAt;
            }

            if (entities.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return entities.Count;
        }
    }
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await _dbContext.Notifications.AddAsync(notification, cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<Notification> notifications, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        await _dbContext.Notifications.AddRangeAsync(notifications, cancellationToken);
    }

    public async Task<IReadOnlyList<Notification>> SuppressPendingActionsForResourceAsync(string resourceType, string resourceId, CancellationToken cancellationToken = default)
    {
        var actionableNotifications = await _dbContext.Notifications
            .Where(n => n.ResourceType == resourceType && n.ResourceId == resourceId && n.IsActionable)
            .ToListAsync(cancellationToken);

        foreach (var notification in actionableNotifications)
        {
            notification.IsActionable = false;
            notification.ActionUrl = null;
        }

        return actionableNotifications;
    }

    public async Task<IReadOnlyList<Notification>> SuppressPendingActionsForOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var orderIdStr = orderId.ToString();
        var actionableNotifications = await _dbContext.Notifications
            .Where(n => (n.OrderId == orderId || (n.ResourceType == "RentalOrder" && n.ResourceId == orderIdStr)) && n.IsActionable)
            .ToListAsync(cancellationToken);

        foreach (var notification in actionableNotifications)
        {
            notification.IsActionable = false;
            notification.ActionUrl = null;
        }

        return actionableNotifications;
    }

    public async Task<IReadOnlyList<Notification>> GetByRentalItemIdAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .Where(n => n.RentalItemId == rentalItemId)
            .ToListAsync(cancellationToken);
    }

    private static readonly string[] ReturnActionNotificationTypes =
    [
        "RETURN_REMINDER_RENTER",
        "RETURN_OVERDUE_SUPPLIER",
        "RETURN_OVERDUE_RENTER",
        "RETURN_CONFIRMATION_OVERDUE_SUPPLIER"
    ];

    private static readonly string[] HandoverActionNotificationTypes =
    [
        "HANDOVER_REMINDER_SUPPLIER",
        "HANDOVER_OVERDUE_SUPPLIER",
        "HANDOVER_OVERDUE_RENTER"
    ];

    public async Task<IReadOnlyList<Notification>> DismissReturnOverdueActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        var actionableNotifications = await _dbContext.Notifications
            .Where(n => n.RentalItemId == rentalItemId
                     && ReturnActionNotificationTypes.Contains(n.NotificationType)
                     && n.IsActionable)
            .ToListAsync(cancellationToken);

        foreach (var notification in actionableNotifications)
        {
            notification.IsActionable = false;
            notification.ActionUrl = null;
        }

        return actionableNotifications;
    }

    public Task<IReadOnlyList<Notification>> DismissReturnActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default) =>
        DismissReturnOverdueActionsForRentalItemAsync(rentalItemId, cancellationToken);

    public async Task<IReadOnlyList<Notification>> DismissHandoverActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        var actionableNotifications = await _dbContext.Notifications
            .Where(n => n.RentalItemId == rentalItemId
                     && HandoverActionNotificationTypes.Contains(n.NotificationType)
                     && n.IsActionable)
            .ToListAsync(cancellationToken);

        foreach (var notification in actionableNotifications)
        {
            notification.IsActionable = false;
            notification.ActionUrl = null;
        }

        return actionableNotifications;
    }

    public async Task<IReadOnlyList<Notification>> SuppressPendingActionsForRentalItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        var itemIdStr = rentalItemId.ToString();
        var actionableNotifications = await _dbContext.Notifications
            .Where(n => (n.RentalItemId == rentalItemId || (n.ResourceType == "RentalItem" && n.ResourceId == itemIdStr)) && n.IsActionable)
            .ToListAsync(cancellationToken);

        foreach (var notification in actionableNotifications)
        {
            notification.IsActionable = false;
            notification.ActionUrl = null;
        }

        return actionableNotifications;
    }

    public async Task<bool> ExistsReminderNotificationAsync(Guid recipientUserId, Guid rentalItemId, string notificationType, string resourceId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.RecipientUserId == recipientUserId
                        && n.RentalItemId == rentalItemId
                        && n.NotificationType == notificationType
                        && n.ResourceId == resourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetRentalItemIdsForOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .Where(n => n.OrderId == orderId && n.RentalItemId != null)
            .Select(n => n.RentalItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasItemHandoverCompletedAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.RentalItemId == rentalItemId && n.NotificationType == "HANDOVER_COMPLETED", cancellationToken);
    }

    public async Task<bool> HasItemReturnedAsync(Guid rentalItemId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .AnyAsync(n => n.RentalItemId == rentalItemId && (n.NotificationType == "RETURN_CONFIRMED" || n.NotificationType == "RENTAL_COMPLETED"), cancellationToken);
    }
}
