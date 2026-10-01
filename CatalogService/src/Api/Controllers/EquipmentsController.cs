using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

/// <summary>Cung cấp API discovery công khai cho thiết bị trong danh mục.</summary>
[ApiController]
[Route("equipments")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class EquipmentsController(ICatalogDiscoveryService discovery) : ControllerBase
{
    /// <summary>Tìm kiếm và phân trang thiết bị đã công khai.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<EquipmentListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<EquipmentListItemDto>>> Search(
        [FromQuery] GetEquipmentsRequest request,
        CancellationToken cancellationToken)
    {
        Result<PagedResult<EquipmentListItemDto>> result = await discovery.SearchAsync(request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết thiết bị theo ID nếu đã công khai.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<EquipmentDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquipmentDetailDto>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<EquipmentDetailDto> result = await discovery.GetDetailAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
