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
            .Include(a => a.Listing)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryAsset?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .Include(a => a.Listing)
            .FirstOrDefaultAsync(a => a.Id == id && a.Listing.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryAsset>> GetAssetsByListingIdAsync(
        Guid listingId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .Where(a => a.EquipmentListingId == listingId)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountAssetsByListingIdAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .CountAsync(a => a.EquipmentListingId == listingId, cancellationToken);
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
            .Include(a => a.Listing)
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
                (al.RentalPeriod.EndDate >= startDate || (al.Status == AllocationStatus.Allocated && al.HandedOverAt != null && al.ReturnedAt == null))));

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
    public async Task<bool> ExistsSerialInListingAsync(
        Guid listingId,
        string serialNumber,
        Guid? excludeAssetId = null,
        CancellationToken cancellationToken = default)
    {
        if (listingId == Guid.Empty || string.IsNullOrWhiteSpace(serialNumber))
        {
            return false;
        }

        var normalized = serialNumber.Trim().ToLowerInvariant();
        var query = _context.Assets
            .Where(a => a.EquipmentListingId == listingId);

        if (excludeAssetId.HasValue && excludeAssetId.Value != Guid.Empty)
        {
            query = query.Where(a => a.Id != excludeAssetId.Value);
        }

#pragma warning disable CA1862 // EF Core LINQ translation to SQL lower(btrim(serial_number))
#pragma warning disable CA1304 // EF Core LINQ translation
#pragma warning disable CA1311 // EF Core LINQ translation
        return await query.AnyAsync(
            a => a.SerialNumber.Trim().ToLower() == normalized,
            cancellationToken);
#pragma warning restore CA1311
#pragma warning restore CA1304
#pragma warning restore CA1862
    }

    /// <inheritdoc />
    public void Update(InventoryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _context.Assets.Update(asset);
    }
}
