using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>
/// Repository quản lý dữ liệu Brand trong Catalog.
/// </summary>
public interface IBrandRepository
{
    Task<PagedResult<Brand>> SearchAsync(BrandSearchQuery query, CancellationToken cancellationToken = default);
    Task<Brand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Brand?> GetActiveByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> IsCodeExistsAsync(string code, CancellationToken cancellationToken = default);
    Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default);
    Task<bool> IsCodeExistsAsync(string code, Guid excludeId, CancellationToken cancellationToken = default);
    Task<bool> IsSlugExistsAsync(string slug, Guid excludeId, CancellationToken cancellationToken = default);
    Task<Result<Brand>> AddAsync(Brand brand, CancellationToken cancellationToken = default);
    Task<Result<Brand>> UpdateAsync(Brand brand, CancellationToken cancellationToken = default);
}
