using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Routing;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

/// <summary>
/// Cung cấp API quản lý và discovery cho canonical Category trong Catalog.
/// </summary>
[ApiController]
[Route("categories")]
[Authorize(Policy = CatalogPolicies.CategoryManage)]
public class CategoryController : ControllerBase
{
    private static readonly HashSet<string> AdminAllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword",
        "page",
        "pageSize",
        "sort",
        "isActive"
    };

    private readonly ICategoryService _categoryService;
    private readonly ICatalogCallerContext _caller;

    public CategoryController(ICategoryService categoryService, ICatalogCallerContext caller)
    {
        _categoryService = categoryService;
        _caller = caller;
    }

    /// <summary>Tìm kiếm và phân trang Category cho Admin (active và inactive, lọc isActive).</summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/categories")]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<PagedResult<CategoryDetailResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<CategoryDetailResponse>>> SearchAdmin(
        [FromQuery] GetAdminCategoriesRequest request,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(k => !AdminAllowedQueryKeys.Contains(k)))
        {
            return this.ToProblem(CatalogErrors.Category.InvalidQuery);
        }

        Result<PagedResult<CategoryDetailResponse>> result = await _categoryService.SearchAdminAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên đọc chi tiết Category theo ID ở mọi trạng thái.</summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/categories/{{id:guid}}")]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryDetailResponse>> GetAdminById(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.GetAdminByIdAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Tạo mới Category, mặc định active.</summary>
    [HttpPost]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.CreateCategoryAsync(request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Cập nhật Category.</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCategory(
        [FromRoute] Guid id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.UpdateCategoryAsync(id, request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Vô hiệu hóa Category (idempotent).</summary>
    [HttpPost("{id:guid}/disable")]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.DisableAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Kích hoạt Category (idempotent).</summary>
    [HttpPost("{id:guid}/enable")]
    [Authorize(Policy = CatalogPolicies.CategoryManage)]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.EnableAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy danh sách tất cả Category đang hoạt động (Public).</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<IEnumerable<CategoryDetailResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetActiveCategoriesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>Lấy chi tiết Category theo ID nếu đang hoạt động (Public).</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategoryById([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.GetActiveCategoryByIdAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    private string GetOperatorUserId() => _caller.UserId?.ToString() ?? string.Empty;
}
