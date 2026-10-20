using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.MediaService.Domain.Repositories;

public class MediaCleanupRepository : IMediaCleanupRepository
{
    private readonly MediaDbContext _context;

    public MediaCleanupRepository(MediaDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task AddAsync(MediaCleanupItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _context.MediaCleanupItems.AddAsync(item, cancellationToken);
    }

    public async Task<IReadOnlyList<MediaCleanupItem>> GetPendingItemsAsync(int limit, CancellationToken cancellationToken = default)
    {
        return await _context.MediaCleanupItems
            .Where(c => c.EligibleAtUtc <= DateTimeOffset.UtcNow &&
                (c.Status == CleanupItemStatus.Pending || (c.Status == CleanupItemStatus.Failed && c.RetryCount < 3)))
            .OrderBy(c => c.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task UpdateAsync(MediaCleanupItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        _context.MediaCleanupItems.Update(item);
        return Task.CompletedTask;
    }

    public async Task RemoveRangeByStorageKeysAsync(IEnumerable<string> storageKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageKeys);
        var keyList = storageKeys.ToList();
        if (keyList.Count == 0) return;

        var items = await _context.MediaCleanupItems
            .Where(c => keyList.Contains(c.StorageKey))
            .ToListAsync(cancellationToken);

        if (items.Count > 0)
        {
            _context.MediaCleanupItems.RemoveRange(items);
        }
    }

    public async Task<IReadOnlyList<MediaCleanupItem>> ClaimPendingItemsAsync(
        int limit, TimeSpan leaseDuration, string workerId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var items = await _context.MediaCleanupItems
            .Where(c => c.EligibleAtUtc <= now &&
                (c.Status == CleanupItemStatus.Pending || (c.Status == CleanupItemStatus.Failed && c.RetryCount < 3)) &&
                (c.LeaseExpiresAtUtc == null || c.LeaseExpiresAtUtc < now))
            .OrderBy(c => c.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (items.Count == 0) return items;

        foreach (var item in items)
        {
            item.ClaimedAtUtc = now;
            item.LeaseExpiresAtUtc = now.Add(leaseDuration);
            item.ClaimedByWorkerId = workerId;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return items;
    }

    public async Task<bool> HasClaimedMarkersAsync(
        IEnumerable<string> storageKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageKeys);
        var keyList = storageKeys.ToList();
        if (keyList.Count == 0) return false;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await _context.MediaCleanupItems
            .AnyAsync(c => keyList.Contains(c.StorageKey) &&
                c.LeaseExpiresAtUtc != null && c.LeaseExpiresAtUtc > now,
                cancellationToken);
    }
}
