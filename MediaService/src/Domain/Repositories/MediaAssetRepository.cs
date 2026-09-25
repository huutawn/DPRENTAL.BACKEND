using Dprental.MediaService.Domain.Entities;
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
}
