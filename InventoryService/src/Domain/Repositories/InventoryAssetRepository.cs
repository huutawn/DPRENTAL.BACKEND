using System.Data;
using System.Globalization;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và lưu trữ thực thể InventoryAsset trên PostgreSQL adapter.
/// </summary>
public sealed partial class InventoryAssetRepository : IInventoryAssetRepository
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
            .Include(a => a.RentalRate)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .Include(a => a.RentalRate)
            .FirstOrDefaultAsync(a => a.Id == id && a.OwnerUserId == ownerUserId, cancellationToken);
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
    public async Task<IReadOnlyList<InventoryAsset>> GetAvailabilitySnapshotsAsync(
        IReadOnlyList<Guid> assetIds,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (assetIds == null || assetIds.Count == 0)
        {
            return Array.Empty<InventoryAsset>();
        }

        var query = _context.Assets
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Where(a => assetIds.Contains(a.Id))
            .Include(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(a => a.Allocations.Where(al =>
                (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                al.RentalPeriod.StartDate <= endDate &&
                al.RentalPeriod.EndDate >= startDate));

        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var results = await query.ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return results;
        }

        return await query.ToListAsync(cancellationToken);
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
