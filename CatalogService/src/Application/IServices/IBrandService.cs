using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.IServices;

/// <summary>
/// Service xử lý nghiệp vụ cho Brand trong Catalog.
/// </summary>
public interface IBrandService
{
    Task<Result<PagedResult<BrandDto>>> SearchPublicAsync(GetBrandsRequest request, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<BrandDto>>> SearchAdminAsync(GetAdminBrandsRequest request, CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> CreateAsync(CreateBrandRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> UpdateAsync(Guid id, UpdateBrandRequest request, string operatorUserId = "", CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> DisableAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<BrandDto>> EnableAsync(Guid id, CancellationToken cancellationToken = default);
}
