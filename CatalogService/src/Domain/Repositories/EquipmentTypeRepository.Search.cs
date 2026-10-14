using System.Data;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public partial class EquipmentTypeRepository
{
    public async Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(
        GetEquipmentTypesRequest request,
        IReadOnlyDictionary<Guid, int> listingCounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(listingCounts);
        var filtered = ApplyFilters(dbContext.EquipmentTypes.AsNoTracking()
            .Where(e => e.Status == EquipmentTypeStatus.Published), request.Keyword, request.CategoryId);
        var counts = CountRelationHelper.Query(dbContext, listingCounts);
        var ranked = from entity in filtered
                     join count in counts on entity.Id equals count.Id into matches
                     from count in matches.DefaultIfEmpty()
                     select new { Entity = entity, ListingCount = (int?)count.ListingCount ?? 0 };
        string sort = request.Sort?.Trim().ToLowerInvariant() ?? "listing_count_desc";
        var ordered = sort switch
        {
            "listing_count_asc" => ranked.OrderBy(r => r.ListingCount == 0).ThenBy(r => r.ListingCount).ThenBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
            "name_asc" => ranked.OrderBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
            "name_desc" => ranked.OrderByDescending(r => r.Entity.Name).ThenBy(r => r.Entity.Id),
            "newest" => ranked.OrderByDescending(r => r.Entity.CreatedAt).ThenBy(r => r.Entity.Id),
            _ => ranked.OrderBy(r => r.ListingCount == 0).ThenByDescending(r => r.ListingCount).ThenBy(r => r.Entity.Name).ThenBy(r => r.Entity.Id)
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        int total = await filtered.CountAsync(cancellationToken);
        var rows = await ordered.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Select(r => new PagedListingCountRow { Id = r.Entity.Id, ListingCount = r.ListingCount })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToArray();
        var items = await dbContext.EquipmentTypes.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new EquipmentTypeListItemDto(
                e.Id, e.Code, e.Name, e.Description,
                e.Categories.OrderBy(c => c.Name).ThenBy(c => c.Id)
                    .Select(c => new CategoryDto(c.Id, c.Name, c.ImageMediaId, null)).ToList(),
                e.Media.OrderByDescending(m => m.IsThumbnail).ThenBy(m => m.DisplayOrder).ThenBy(m => m.Id)
                    .Select(m => (Guid?)m.MediaId).FirstOrDefault(),
                null, e.CurrentSchemaVersion, 0))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var byId = items.ToDictionary(e => e.Id);
        var result = rows.Select(r => byId[r.Id] with { ListingCount = r.ListingCount }).ToList();
        return new PagedResult<EquipmentTypeListItemDto>(result, request.Page, request.PageSize, total);
    }
}
