using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Services;

public interface ICategoryService
{
    Task<Result<CategoryDetailResponse>> CreateCategoryAsync(CreateCategoryRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<CategoryDetailResponse>>> GetActiveCategoriesAsync(GetCategoriesRequest request, CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> GetActiveCategoryByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<CategoryDetailResponse>>> SearchAdminAsync(GetAdminCategoriesRequest request, CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> DisableAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<CategoryDetailResponse>> EnableAsync(Guid id, CancellationToken cancellationToken = default);
}
