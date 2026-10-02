using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và lưu trữ thực thể InventoryAsset trên PostgreSQL.
/// </summary>
public sealed class InventoryAssetRepository : IInventoryAssetRepository
{
    private readonly InventoryServiceDbContext _context;

    public InventoryAssetRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .FirstOrDefaultAsync(a => a.Id == id && a.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> ListByOwnerAsync(Guid ownerUserId, int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> ListAllAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
    /// <inheritdoc />
    public async Task<InventoryAsset?> GetCalendarSnapshotAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .AsNoTrackingWithIdentityResolution()
            .AsSingleQuery()
            .Include(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(a => a.Allocations.Where(al =>
                (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                al.RentalPeriod.StartDate <= endDate &&
                al.RentalPeriod.EndDate >= startDate))
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _context.Assets.Add(asset);
    }

    /// <inheritdoc />
    public void Update(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _context.Assets.Update(asset);
    }
}
