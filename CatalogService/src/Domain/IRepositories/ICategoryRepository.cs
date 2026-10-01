using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default);
    Task AddAsync(Category category, CancellationToken cancellationToken = default);
    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);
    Task<IEnumerable<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default);
}