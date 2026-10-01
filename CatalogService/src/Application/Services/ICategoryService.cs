using CatalogService.Application.DTOs.Category;

namespace Dprental.CatalogService.Application.Services;

public interface ICategoryService
{
    Task<CategoryDetailResponse> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default);
    Task<CategoryDetailResponse?> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken = default);
    Task<IEnumerable<CategoryDetailResponse>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CategoryDetailResponse?> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default);
}