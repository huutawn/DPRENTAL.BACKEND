using System.Data;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và lưu trữ thực thể EquipmentListing trên PostgreSQL.
/// </summary>
public sealed partial class EquipmentListingRepository : IEquipmentListingRepository
{
    private readonly InventoryServiceDbContext _context;

    public EquipmentListingRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<EquipmentListing?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.EquipmentListings
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EquipmentListing?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.EquipmentListings
            .FirstOrDefaultAsync(l => l.Id == id && l.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EquipmentListing?> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.EquipmentListings
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id && l.Status == EquipmentListingStatus.Active, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EquipmentListing>> ListForOwnerAsync(
        Guid ownerUserId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.EquipmentListings
            .Where(l => l.OwnerUserId == ownerUserId)
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.EquipmentListings
            .CountAsync(l => l.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetTotalQuantityAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        return await _context.Assets
            .CountAsync(a => a.EquipmentListingId == listingId && a.AcceptingBookings, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EquipmentListing?> GetCalendarSnapshotAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var query = _context.EquipmentListings
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Where(l => l.Id == listingId)
            .Include(l => l.Assets)
                .ThenInclude(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(l => l.Assets)
                .ThenInclude(a => a.Allocations.Where(al =>
                    (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                    al.RentalPeriod.StartDate <= endDate &&
                    al.RentalPeriod.EndDate >= startDate));

        if (_context.Database.IsRelational() && _context.Database.CurrentTransaction is null)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var result = await query.FirstOrDefaultAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(EquipmentListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        _context.EquipmentListings.Add(listing);
    }

    /// <inheritdoc />
    public void Update(EquipmentListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        _context.EquipmentListings.Update(listing);
    }
}
