using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

[ApiController]
[Route("api/v1/catalog/categories")]
[Authorize(Roles = "Admin")]
public class CategoryController : ControllerBase
{
    private readonly ICategoryService _categoryService;

    public CategoryController(ICategoryService categoryService)
    {
        _categoryService = categoryService;
    }

    // POST /api/v1/catalog/categories
    [HttpPost]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await _categoryService.CreateCategoryAsync(request, cancellationToken);

        // Trả về mã 201 Created cùng với data của danh mục vừa tạo
        return StatusCode(StatusCodes.Status201Created, result);
    }

    // PATCH /api/v1/catalog/categories/{id}
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateCategory(
        [FromRoute] Guid id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _categoryService.UpdateCategoryAsync(id, request, cancellationToken);

        if (result == null)
        {
            return NotFound(new { message = $"Category with id '{id}' was not found." });
        }

        // Trả về mã 200 OK cùng với data đã được cập nhật
        return Ok(result);
    }

    // GET /api/v1/catalog/categories
    [AllowAnonymous] // Cho phép user chưa đăng nhập (Public) gọi API này
    [HttpGet]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetActiveCategoriesAsync(cancellationToken);
        return Ok(result);
    }

    // GET /api/v1/catalog/categories/{id}
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCategoryById([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetActiveCategoryByIdAsync(id, cancellationToken);

        if (result == null)
        {
            return NotFound(new { message = "Category not found or is inactive." });
        }

        return Ok(result);
    }
}