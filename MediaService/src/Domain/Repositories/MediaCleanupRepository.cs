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
}
