using System.Text.Json;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public class CatalogRepository(CatalogServiceDbContext dbContext) : IEquipmentQueryRepository
{
    public async Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(
        GetEquipmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = dbContext.Equipments.AsNoTracking()
            .Where(e => e.Status == EquipmentStatus.Published);

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            query = query.Where(e =>
                EF.Functions.ILike(e.Name, $"%{keyword}%") ||
                EF.Functions.ILike(e.Brand ?? string.Empty, $"%{keyword}%") ||
                EF.Functions.ILike(e.Code, $"%{keyword}%"));
        }

        if (request.CategoryId.HasValue)
        {
            query = query.Where(e => e.CategoryId == request.CategoryId.Value);
        }

        if (request.RegionId.HasValue)
        {
            query = query.Where(e => e.RegionId == request.RegionId.Value);
        }

        if (request.MinPrice.HasValue)
        {
            query = query.Where(e => e.DailyPrice >= request.MinPrice.Value);
        }

        if (request.MaxPrice.HasValue)
        {
            query = query.Where(e => e.DailyPrice <= request.MaxPrice.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        string sort = request.Sort?.Trim().ToLowerInvariant() ?? string.Empty;
        query = sort switch
        {
            "price_asc" => query.OrderBy(e => e.DailyPrice).ThenBy(e => e.Id),
            "price_desc" => query.OrderByDescending(e => e.DailyPrice).ThenBy(e => e.Id),
            _ => query.OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)
        };

        var skip = (request.Page - 1) * request.PageSize;
        var items = await query
            .Skip(skip)
            .Take(request.PageSize)
            .Select(e => new EquipmentListItemDto(
                e.Id,
                e.Name ?? string.Empty,
                e.Brand,
                e.DailyPrice,
                e.RegionId,
                e.Media!.OrderByDescending(m => m.IsThumbnail).ThenBy(m => m.DisplayOrder).Select(m => (Guid?)m.MediaId).FirstOrDefault(),
                new CategoryDto(e.CategoryId, e.Category!.Name ?? string.Empty)
            ))
            .ToListAsync(cancellationToken);

        return new PagedResult<EquipmentListItemDto>(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<EquipmentDetailDto?> GetPublishedByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.Equipments.AsNoTracking()
            .Where(e => e.Id == id && e.Status == EquipmentStatus.Published)
            .Select(e => new
            {
                e.Id,
                Code = e.Code ?? string.Empty,
                Name = e.Name ?? string.Empty,
                e.Brand,
                e.Description,
                e.DailyPrice,
                e.RegionId,
                CategoryId = e.CategoryId,
                CategoryName = e.Category!.Name ?? string.Empty,
                e.Specifications,
                MediaIds = e.Media!.OrderBy(m => m.DisplayOrder).Select(m => m.MediaId).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        JsonElement? specificationsElement = null;
        if (!string.IsNullOrWhiteSpace(row.Specifications))
        {
            using JsonDocument document = JsonDocument.Parse(row.Specifications);
            specificationsElement = document.RootElement.Clone();
        }

        return new EquipmentDetailDto(
            row.Id,
            row.Code,
            row.Name,
            row.Brand,
            row.Description,
            row.DailyPrice,
            row.RegionId,
            new CategoryDto(row.CategoryId, row.CategoryName),
            specificationsElement,
            row.MediaIds);
    }
}
