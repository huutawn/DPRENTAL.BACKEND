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
    public async Task<IReadOnlyList<EquipmentListingBatchItem>> GetBatchByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return Array.Empty<EquipmentListingBatchItem>();
        }

        return await _context.EquipmentListings
            .AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new EquipmentListingBatchItem(
                l.Id,
                l.OwnerUserId,
                l.EquipmentTypeId,
                l.Name,
                l.ThumbnailMediaId,
                l.DailyRate,
                l.Currency,
                l.Status,
                l.DepositAmount,
                l.BrandId,
                l.Model,
                l.SchemaVersion,
                l.SpecValues,
                l.Warehouse != null ? new EquipmentListingBatchWarehouse(
                    l.Warehouse.AddressDetail,
                    l.Warehouse.ProvinceCode,
                    l.Warehouse.ProvinceName,
                    l.Warehouse.WardCode,
                    l.Warehouse.WardName,
                    l.Warehouse.Latitude,
                    l.Warehouse.Longitude) : null))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EquipmentListingStatisticsResult> GetListingStatisticsAsync(
        EquipmentListingStatisticsCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var baseQuery = _context.EquipmentListings
            .AsNoTracking();

        if (criteria.OwnerUserId.HasValue)
        {
            baseQuery = baseQuery.Where(l => l.OwnerUserId == criteria.OwnerUserId.Value);
        }

        if (criteria.Status.HasValue)
        {
            baseQuery = baseQuery.Where(l => l.Status == criteria.Status.Value);
        }

        var groupedCounts = await baseQuery
            .GroupBy(l => new { l.EquipmentTypeId, l.BrandId })
            .Select(g => new
            {
                g.Key.EquipmentTypeId,
                g.Key.BrandId,
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

        var byEquipmentType = new Dictionary<Guid, int>();
        var byBrand = new Dictionary<Guid, int>();

        foreach (var item in groupedCounts)
        {
            byEquipmentType[item.EquipmentTypeId] = byEquipmentType.GetValueOrDefault(item.EquipmentTypeId, 0) + item.Count;
            byBrand[item.BrandId] = byBrand.GetValueOrDefault(item.BrandId, 0) + item.Count;
        }

        return new EquipmentListingStatisticsResult(byEquipmentType, byBrand);
    }

    /// <inheritdoc />
    public async Task<OwnerEquipmentListingSearchResult> SearchOwnerAsync(
        OwnerEquipmentListingSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (criteria.CandidateIds != null && criteria.CandidateIds.Count == 0)
        {
            return new OwnerEquipmentListingSearchResult(Array.Empty<OwnerEquipmentListingItem>(), 0);
        }

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

            if (criteria.CandidateIds != null)
            {
                baseQuery = baseQuery.Where(l => criteria.CandidateIds.Contains(l.Id));
            }
            else if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
            {
                var escaped = EscapeLikePattern(criteria.SearchKeyword);
                var pattern = $"%{escaped}%";
                baseQuery = baseQuery.Where(l =>
                    EF.Functions.ILike(l.Name, pattern, @"\") ||
                    EF.Functions.ILike(l.Model, pattern, @"\"));
            }

            if (criteria.Sort == AssetSortOption.Relevance && criteria.CandidateIds != null)
            {
                var matchingIds = (await baseQuery.Select(l => l.Id).ToListAsync(cancellationToken)).ToHashSet();
                var orderedMatchingIds = criteria.CandidateIds.Where(id => matchingIds.Contains(id)).ToList();
                int totalCount = orderedMatchingIds.Count;
                if (totalCount == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new OwnerEquipmentListingSearchResult(Array.Empty<OwnerEquipmentListingItem>(), 0);
                }

                var pagedIds = orderedMatchingIds.Skip(criteria.Skip).Take(criteria.Take).ToList();
                var relCandidateRows = await _context.EquipmentListings
                    .AsNoTracking()
                    .Where(l => pagedIds.Contains(l.Id))
                    .Select(l => new
                    {
                        Listing = l,
                        TotalQuantity = l.Assets.Count(a => a.AcceptingBookings)
                    })
                    .ToListAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                var rowMap = relCandidateRows.ToDictionary(r => r.Listing.Id);
                var items = pagedIds
                    .Where(id => rowMap.ContainsKey(id))
                    .Select(id => new OwnerEquipmentListingItem(rowMap[id].Listing, rowMap[id].TotalQuantity))
                    .ToList();

                return new OwnerEquipmentListingSearchResult(items, totalCount);
            }
            else
            {
                int totalCount = await baseQuery.CountAsync(cancellationToken);
                if (totalCount == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new OwnerEquipmentListingSearchResult(Array.Empty<OwnerEquipmentListingItem>(), 0);
                }

                var orderedQuery = criteria.Sort switch
                {
                    AssetSortOption.CreatedAsc => baseQuery.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id),
                    AssetSortOption.ModelDesc => baseQuery.OrderByDescending(l => l.Model).ThenByDescending(l => l.Id),
                    AssetSortOption.ModelAsc => baseQuery.OrderBy(l => l.Model).ThenBy(l => l.Id),
                    _ => baseQuery.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
                };

                var rows = await orderedQuery
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

        if (criteria.CandidateIds != null)
        {
            nonRelQuery = nonRelQuery.Where(l => criteria.CandidateIds.Contains(l.Id));
        }
        else if (!string.IsNullOrWhiteSpace(criteria.SearchKeyword))
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
            .Include(l => l.Warehouse)
            .Include(l => l.Assets)
                .ThenInclude(a => a.CalendarBlocks.Where(cb => cb.StartDate <= endDate && cb.EndDate >= startDate))
            .Include(l => l.Assets)
                .ThenInclude(a => a.Allocations.Where(al =>
                    (al.Status == AllocationStatus.Held || al.Status == AllocationStatus.Allocated) &&
                    al.RentalPeriod.StartDate <= endDate &&
                    (al.RentalPeriod.EndDate >= startDate || (al.Status == AllocationStatus.Allocated && al.HandedOverAt != null && al.ReturnedAt == null))));

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
    public async Task<IReadOnlyList<Guid>> GetListingIdsByOwnerKeysetAsync(
        Guid ownerUserId,
        Guid? afterListingId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = _context.EquipmentListings
            .AsNoTracking()
            .Where(l => l.OwnerUserId == ownerUserId);

        if (afterListingId.HasValue && afterListingId.Value != Guid.Empty)
        {
            query = query.Where(l => l.Id > afterListingId.Value);
        }

        return await query
            .OrderBy(l => l.Id)
            .Take(limit)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);
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
