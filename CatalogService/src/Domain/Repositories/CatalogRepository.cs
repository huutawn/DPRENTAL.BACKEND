using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public class CatalogRepository : IEquipmentQueryRepository
{
    private readonly CatalogServiceDbContext _dbContext;

    public CatalogRepository(CatalogServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<EquipmentListItem>> SearchPublishedAsync(
        EquipmentSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Equipments.AsNoTracking()
            .Where(e => e.Status == EquipmentStatus.Published);

        if (!string.IsNullOrWhiteSpace(criteria.Keyword))
        {
            // Thêm dấu ? và fallback chuỗi rỗng để an toàn tuyệt đối
            var keyword = criteria.Keyword?.Trim() ?? string.Empty;
            query = query.Where(e =>
                EF.Functions.ILike(e.Name, $"%{keyword}%") ||
                EF.Functions.ILike(e.Brand ?? string.Empty, $"%{keyword}%") ||
                EF.Functions.ILike(e.Code, $"%{keyword}%"));
        }

        if (criteria.CategoryId.HasValue)
            query = query.Where(e => e.CategoryId == criteria.CategoryId.Value);

        if (criteria.RegionId.HasValue)
            query = query.Where(e => e.RegionId == criteria.RegionId.Value);

        if (criteria.MinPrice.HasValue)
            query = query.Where(e => e.DailyPrice >= criteria.MinPrice.Value);

        if (criteria.MaxPrice.HasValue)
            query = query.Where(e => e.DailyPrice <= criteria.MaxPrice.Value);

        var totalItems = await query.CountAsync(cancellationToken);

        query = criteria.Sort switch
        {
            EquipmentSort.PriceAscending => query.OrderBy(e => e.DailyPrice).ThenBy(e => e.Id),
            EquipmentSort.PriceDescending => query.OrderByDescending(e => e.DailyPrice).ThenBy(e => e.Id),
            _ => query.OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)
        };

        var items = await query
            .Skip((criteria.Page - 1) * criteria.PageSize)
            .Take(criteria.PageSize)
            .Select(e => new EquipmentListItem(
                e.Id,
                e.Name ?? string.Empty, // Đảm bảo Name không null
                e.Brand,
                e.DailyPrice,
                e.RegionId,
                e.Media!.OrderByDescending(m => m.IsThumbnail).ThenBy(m => m.DisplayOrder).Select(m => (Guid?)m.MediaId).FirstOrDefault(),
                new CategorySummary(e.CategoryId, e.Category!.Name ?? string.Empty) // Dùng thẳng CategoryId và báo hiệu Category không null
            ))
            .ToListAsync(cancellationToken);

        return new PagedResult<EquipmentListItem>(items, totalItems);
    }

    public async Task<EquipmentDetail?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Equipments.AsNoTracking()
            .Where(e => e.Id == id && e.Status == EquipmentStatus.Published)
            .Select(e => new EquipmentDetail(
                e.Id,
                e.Code ?? string.Empty,
                e.Name ?? string.Empty,
                e.Brand,
                e.Description,
                e.DailyPrice,
                e.RegionId,
                new CategorySummary(e.CategoryId, e.Category!.Name ?? string.Empty),
                e.Specifications,
                e.Media!.OrderBy(m => m.DisplayOrder).Select(m => m.MediaId).ToList()
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }
}