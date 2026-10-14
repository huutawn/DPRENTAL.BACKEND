using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public class CategoryRepository : ICategoryRepository
{
    private readonly CatalogServiceDbContext _dbContext;

    public CategoryRepository(CatalogServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<Category>> SearchAsync(CategorySearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dbQuery = _dbContext.Categories.AsNoTracking();

        if (query.IsActive.HasValue)
        {
            dbQuery = dbQuery.Where(c => c.IsActive == query.IsActive.Value);
        }

        var trimmedKeyword = query.Keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedKeyword))
        {
            dbQuery = dbQuery.Where(c =>
                EF.Functions.ILike(c.Name, $"%{trimmedKeyword}%") ||
                EF.Functions.ILike(c.Slug, $"%{trimmedKeyword}%"));
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        string sort = query.Sort?.Trim().ToLowerInvariant() ?? string.Empty;
        dbQuery = sort switch
        {
            "name_asc" => dbQuery.OrderBy(c => c.Name).ThenBy(c => c.Id),
            "name_desc" => dbQuery.OrderByDescending(c => c.Name).ThenBy(c => c.Id),
            _ => dbQuery.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id)
        };

        var skip = (query.Page - 1) * query.PageSize;
        var items = await dbQuery
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Category>(items, query.Page, query.PageSize, totalCount);
    }
    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Chú ý: Không dùng AsNoTracking() ở đây vì ta cần update entity này
        return await _dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Categories.AnyAsync(c => c.Slug == slug, cancellationToken);
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        await _dbContext.Categories.AddAsync(category, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        _dbContext.Categories.Update(category);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CategoryWithPublishedTypes>> GetActiveCategoriesWithPublishedTypesAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _dbContext.Categories
            .Where(c => c.IsActive)
            .AsNoTracking()
            .Select(c => new
            {
                Category = c,
                PublishedTypeIds = c.EquipmentTypes
                    .Where(t => t.Status == Dprental.CatalogService.Common.Enums.EquipmentTypeStatus.Published)
                    .Select(t => t.Id)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return raw
            .Select(x => new CategoryWithPublishedTypes(x.Category, x.PublishedTypeIds))
            .ToList();
    }
}