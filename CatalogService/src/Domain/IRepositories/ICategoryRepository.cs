using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface ICategoryRepository
{
    Task<PagedResult<Category>> SearchAsync(CategorySearchQuery query, CancellationToken cancellationToken = default);
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default);
    Task AddAsync(Category category, CancellationToken cancellationToken = default);
    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CategoryWithPublishedTypes>> GetActiveCategoriesWithPublishedTypesAsync(CancellationToken cancellationToken = default);
}