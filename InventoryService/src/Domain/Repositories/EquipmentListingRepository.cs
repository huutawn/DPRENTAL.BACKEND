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
    public async Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(
        OwnerEquipmentListingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

            var baseQuery = _context.EquipmentListings
                .AsNoTracking()
                .Where(l => l.OwnerUserId == criteria.OwnerUserId);
            if (criteria.EquipmentTypeId.HasValue)
            {
                baseQuery = baseQuery.Where(l => l.EquipmentTypeId == criteria.EquipmentTypeId.Value);
            }

            if (criteria.BrandId.HasValue)
            {
                baseQuery = baseQuery.Where(l => l.BrandId == criteria.BrandId.Value);
            }

            if (criteria.Status.HasValue)
            {
                baseQuery = baseQuery.Where(l => l.Status == criteria.Status.Value);
            }

            if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
            {
                var escaped = EscapeLikePattern(criteria.SearchKeyword);
                var pattern = $"%{escaped}%";
                baseQuery = baseQuery.Where(l =>
                    EF.Functions.ILike(l.Name, pattern, @"\") ||
                    EF.Functions.ILike(l.Model, pattern, @"\"));
            }

            int totalCount = await baseQuery.CountAsync(cancellationToken);
            if (totalCount == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new OwnerEquipmentListingSearchResult(Array.Empty<OwnerEquipmentListingItem>(), 0);
            }

            var rows = await baseQuery
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Skip(criteria.Skip)
                .Take(criteria.Take)
                .Select(l => new
                {
                    Listing = l,
                    TotalQuantity = l.Assets.Count(a => a.AcceptingBookings)
                })
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var items = rows
                .Select(r => new OwnerEquipmentListingItem(r.Listing, r.TotalQuantity))
                .ToList();

            return new OwnerEquipmentListingSearchResult(items, totalCount);
        }

        var nonRelQuery = _context.EquipmentListings
            .AsNoTracking()
            .Where(l => l.OwnerUserId == criteria.OwnerUserId);
        if (criteria.EquipmentTypeId.HasValue)
        {
            nonRelQuery = nonRelQuery.Where(l => l.EquipmentTypeId == criteria.EquipmentTypeId.Value);
        }

        if (criteria.BrandId.HasValue)
        {
            nonRelQuery = nonRelQuery.Where(l => l.BrandId == criteria.BrandId.Value);
        }

        if (criteria.Status.HasValue)
        {
            nonRelQuery = nonRelQuery.Where(l => l.Status == criteria.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
        {
            nonRelQuery = nonRelQuery.Where(l =>
                l.Name.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                l.Model.Contains(criteria.SearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        int count = await nonRelQuery.CountAsync(cancellationToken);
        if (count == 0)
        {
            return new OwnerEquipmentListingSearchResult(Array.Empty<OwnerEquipmentListingItem>(), 0);
        }

        var pagedRows = await nonRelQuery
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Skip(criteria.Skip)
            .Take(criteria.Take)
            .Select(l => new
            {
                Listing = l,
                TotalQuantity = l.Assets.Count(a => a.AcceptingBookings)
            })
            .ToListAsync(cancellationToken);

        var pagedItems = pagedRows
            .Select(r => new OwnerEquipmentListingItem(r.Listing, r.TotalQuantity))
            .ToList();

        return new OwnerEquipmentListingSearchResult(pagedItems, count);
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
