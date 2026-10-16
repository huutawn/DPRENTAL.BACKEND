using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.MediaService.Domain.Repositories;

public class MediaAssetRepository : IMediaAssetRepository
{
    private readonly MediaDbContext _context;

    public MediaAssetRepository(MediaDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<MediaAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MediaAssets
            .Include(a => a.ImageVariants)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<MediaAsset?> GetByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MediaAssets
            .Include(a => a.ImageVariants)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task AddAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        await _context.MediaAssets.AddAsync(asset, cancellationToken);
    }

    public Task UpdateAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        asset.UpdatedAtUtc = DateTimeOffset.UtcNow;
        asset.Version++;
        _context.MediaAssets.Update(asset);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MediaAsset>> GetReadyImagesMissingVariantsAsync(
        int batchSize,
        Guid? afterId,
        int processingVersion,
        CancellationToken cancellationToken = default)
    {
        var query = _context.MediaAssets
            .Include(a => a.ImageVariants)
            .Where(a => a.Status == MediaStatus.Ready)
            .Where(a => a.ActualContentType == "image/jpeg" || a.ActualContentType == "image/png" || a.ActualContentType == "image/webp")
            .Where(a => a.ImageVariants.Count(v => v.ProcessingVersion == processingVersion) < 3);

        if (afterId.HasValue)
        {
            query = query.Where(a => a.Id > afterId.Value);
        }

        return await query
            .OrderBy(a => a.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddVariantsAsync(IEnumerable<MediaImageVariant> variants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variants);
        await _context.MediaImageVariants.AddRangeAsync(variants, cancellationToken);
    }
}
