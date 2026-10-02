using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Application.Services;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

[ApiController]
[Route("categories")]
[Authorize(Roles = "adm")]
public class CategoryController : ControllerBase
{
    private readonly ICategoryService _categoryService;

    public CategoryController(ICategoryService categoryService)
    {
        _categoryService = categoryService;
    }

    // POST /api/v1/catalog/categories
    [HttpPost]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.CreateCategoryAsync(request, cancellationToken);

        // Trả về mã 201 Created cùng với data của danh mục vừa tạo
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    // PATCH /api/v1/catalog/categories/{id}
    [HttpPatch("{id:guid}")]
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
        Result<CategoryDetailResponse> result = await _categoryService.UpdateCategoryAsync(id, request, cancellationToken);

        // Trả về mã 200 OK cùng với data đã được cập nhật
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    // GET /api/v1/catalog/categories
    [AllowAnonymous] // Cho phép user chưa đăng nhập (Public) gọi API này
    [HttpGet]
    [ProducesResponseType<IEnumerable<CategoryDetailResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetActiveCategoriesAsync(cancellationToken);
        return Ok(result);
    }

    // GET /api/v1/catalog/categories/{id}
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<CategoryDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategoryById([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        Result<CategoryDetailResponse> result = await _categoryService.GetActiveCategoryByIdAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
