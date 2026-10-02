using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

/// <summary>
/// Equipment type của Canonical Catalog. Admin quản lý vòng đời (tạo, sửa, publish, disable);
/// mọi người đọc được các type đang Published.
/// </summary>
[ApiController]
[Route("equipment-types")]
[Authorize(Policy = CatalogPolicies.CatalogAdmin)]
public sealed class EquipmentTypesController(
    IEquipmentTypeManagementService managementService,
    IEquipmentTypeCatalogService catalogService) : ControllerBase
{
    /// <summary>Admin tạo equipment type mới ở trạng thái Draft.</summary>
    [HttpPost]
    [ProducesResponseType<EquipmentTypeManagementResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateEquipmentTypeRequest request, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeManagementResponse> result = await managementService.CreateAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return CreatedAtAction(nameof(GetForManagement), new { id = result.Value.Id }, result.Value);
    }

    /// <summary>Admin cập nhật các field được phép sửa; field bỏ trống được giữ nguyên.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<EquipmentTypeManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid id, UpdateEquipmentTypeRequest request, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeManagementResponse> result = await managementService.UpdateAsync(id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Admin xem chi tiết equipment type ở mọi trạng thái.</summary>
    [HttpGet("{id:guid}/management")]
    [ProducesResponseType<EquipmentTypeManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForManagement(Guid id, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeManagementResponse> result = await managementService.GetForManagementAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Admin công khai equipment type Draft hoặc Disabled sau khi kiểm tra điều kiện publish.</summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType<EquipmentTypeManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeManagementResponse> result = await managementService.PublishAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Admin ngừng dùng equipment type đang Published; dữ liệu không bị xóa.</summary>
    [HttpPost("{id:guid}/disable")]
    [ProducesResponseType<EquipmentTypeManagementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeManagementResponse> result = await managementService.DisableAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Danh sách công khai các equipment type đang Published, có thể lọc theo category.</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EquipmentTypeResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPublished([FromQuery] Guid? categoryId, CancellationToken cancellationToken) =>
        Ok(await catalogService.ListPublishedAsync(categoryId, cancellationToken));

    /// <summary>Chi tiết công khai một equipment type; type Draft hoặc Disabled trả 404.</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<EquipmentTypeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublished(Guid id, CancellationToken cancellationToken)
    {
        Result<EquipmentTypeResponse> result = await catalogService.GetPublishedAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
