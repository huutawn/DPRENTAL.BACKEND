using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Routing;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

/// <summary>Cung cấp API quản lý và discovery cho loại thiết bị trong Catalog.</summary>
[ApiController]
[Route("equipment-types")]
[Produces("application/json")]
public sealed partial class EquipmentTypesController(IEquipmentTypeService service, ICatalogCallerContext caller) : ControllerBase
{
    private static readonly HashSet<string> PublicAllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword", "categoryId", "page", "pageSize", "sort", "scope", "listingStatus"
    };

    private static readonly HashSet<string> AdminAllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "keyword",
        "categoryId",
        "page",
        "pageSize",
        "sort",
        "status"
    };

    /// <summary>Tìm kiếm và phân trang loại thiết bị đã công khai.</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType<PagedResult<EquipmentTypeListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PagedResult<EquipmentTypeListItemDto>>> Search(
        [FromQuery] GetEquipmentTypesRequest request,
        CancellationToken cancellationToken)
    {

        if (Request.Query.Any(q => !PublicAllowedQueryKeys.Contains(q.Key) || q.Value.Count != 1))
        {
            return this.ToProblem(CatalogErrors.EquipmentType.InvalidQuery);
        }

        Result<PagedResult<EquipmentTypeListItemDto>> result = await service.SearchAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>
    /// Quản trị viên tìm kiếm và phân trang loại thiết bị ở mọi trạng thái (Draft, Published, Disabled).
    /// Lọc bằng <c>status</c>, lặp lại tham số để lấy nhiều trạng thái.
    /// </summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/equipment-types")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<PagedResult<EquipmentTypeAdminListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<EquipmentTypeAdminListItemDto>>> SearchAdmin(
        [FromQuery] GetAdminEquipmentTypesRequest request,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(k => !AdminAllowedQueryKeys.Contains(k)))
        {
            return this.ToProblem(CatalogErrors.EquipmentType.InvalidQuery);
        }

        Result<PagedResult<EquipmentTypeAdminListItemDto>> result = await service.SearchAdminAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Quản trị viên đọc chi tiết loại thiết bị theo ID ở mọi trạng thái.</summary>
    [HttpGet($"/{ApiRoutes.Prefix}/admin/equipment-types/{{id:guid}}")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquipmentTypeDetailDto>> GetAdminById(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.GetAdminDetailAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết loại thiết bị theo ID nếu đã công khai.</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquipmentTypeDetailDto>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.GetPublishedDetailAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Tạo mới loại thiết bị ở trạng thái Draft.</summary>
    [HttpPost]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateEquipmentTypeRequest request,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.CreateAsync(request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Cập nhật thông tin canonical và danh sách media của loại thiết bị.</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateEquipmentTypeRequest request,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.UpdateAsync(id, request, GetOperatorUserId(), cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Công khai loại thiết bị lên public catalog từ trạng thái Draft hoặc Disabled.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.PublishAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Vô hiệu hóa loại thiết bị đang công khai khỏi public catalog.</summary>
    [HttpPost("{id:guid}/disable")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disable(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeDetailDto> result = await service.DisableAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    private string GetOperatorUserId() => caller.UserId?.ToString() ?? string.Empty;
}
