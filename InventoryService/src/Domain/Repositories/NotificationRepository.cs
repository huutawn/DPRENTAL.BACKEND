using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository quản lý các thông báo vận hành của tổ chức (Notification) bằng EF Core.
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo repository với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public NotificationRepository(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Notification?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Notification?> GetByDeduplicationKeyAsync(
        Guid organizationId,
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .FirstOrDefaultAsync(n => n.OrganizationId == organizationId && n.DeduplicationKey == deduplicationKey, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Notification>> ListUnreadByOrganizationAsync(
        Guid organizationId,
        DateTimeOffset now,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .AsNoTracking()
            .Where(n => n.OrganizationId == organizationId && n.ViewedAt == null && n.ExpiresAt > now)
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Notification>> ListAllByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Notifications
            .AsNoTracking()
            .Where(n => n.OrganizationId == organizationId)
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(Notification notification)
    {
        _context.Notifications.Add(notification);
    }

    /// <inheritdoc />
    public void Update(Notification notification)
    {
        var entry = _context.Entry(notification);
        if (entry.State == EntityState.Detached)
        {
            _context.Notifications.Update(notification);
        }
    }
}
