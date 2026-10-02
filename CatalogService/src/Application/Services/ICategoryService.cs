using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Services;

public interface ICategoryService
{
    Task<Result<CategoryDetailResponse>> CreateCategoryAsync(CreateCategoryRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<IEnumerable<CategoryDetailResponse>> GetActiveCategoriesAsync(CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
