using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Routing;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

/// <summary>
/// Cung cấp API quản lý và discovery cho canonical Brand trong Catalog.
/// </summary>
[ApiController]
[Route("brands")]
[Produces("application/json")]
public sealed class BrandsController(IBrandService service, ICatalogCallerContext caller) : ControllerBase
{

    private static readonly HashSet<string> PublicAllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword", "page", "pageSize", "sort", "scope", "listingStatus"
    };

    private static readonly HashSet<string> AdminAllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword",
        "page",
        "pageSize",
        "sort",
        "isActive"
    };

    /// <summary>Tìm kiếm và phân trang Brand công khai (chỉ active).</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<PagedResult<BrandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PagedResult<BrandDto>>> SearchPublic(
        [FromQuery] GetBrandsRequest request,
        CancellationToken cancellationToken)
    {

        if (Request.Query.Any(q => !PublicAllowedQueryKeys.Contains(q.Key) || q.Value.Count != 1))
        {
            return this.ToProblem(CatalogErrors.Brand.InvalidQuery);
        }

        Result<PagedResult<BrandDto>> result = await service.SearchPublicAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết Brand theo ID nếu đang active.</summary>
    [AllowAnonymous]
    [HttpGet("{brandId:guid}")]
    [ProducesResponseType<BrandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrandDto>> GetPublicById(
        Guid brandId,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.GetPublicByIdAsync(brandId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên tìm kiếm và phân trang Brand (active và inactive, lọc isActive).</summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/brands")]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<PagedResult<BrandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<BrandDto>>> SearchAdmin(
        [FromQuery] GetAdminBrandsRequest request,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(k => !AdminAllowedQueryKeys.Contains(k)))
        {
            return this.ToProblem(CatalogErrors.Brand.InvalidQuery);
        }

        Result<PagedResult<BrandDto>> result = await service.SearchAdminAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên đọc chi tiết Brand theo ID ở mọi trạng thái.</summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/brands/{{brandId:guid}}")]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<BrandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BrandDto>> GetAdminById(
        Guid brandId,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.GetAdminByIdAsync(brandId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Tạo mới Brand, mặc định active.</summary>
    [HttpPost]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<BrandDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateBrandRequest request,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.CreateAsync(request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Cập nhật thông tin Brand.</summary>
    [HttpPatch("{brandId:guid}")]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<BrandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid brandId,
        [FromBody] UpdateBrandRequest request,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.UpdateAsync(brandId, request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Vô hiệu hóa Brand (idempotent).</summary>
    [HttpPost("{brandId:guid}/disable")]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<BrandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(
        Guid brandId,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.DisableAsync(brandId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Kích hoạt Brand (idempotent).</summary>
    [HttpPost("{brandId:guid}/enable")]
    [Authorize(Policy = CatalogPolicies.BrandManage)]
    [ProducesResponseType<BrandDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(
        Guid brandId,
        CancellationToken cancellationToken)
    {
        Result<BrandDto> result = await service.EnableAsync(brandId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    private string GetOperatorUserId() => caller.UserId?.ToString() ?? string.Empty;
}
