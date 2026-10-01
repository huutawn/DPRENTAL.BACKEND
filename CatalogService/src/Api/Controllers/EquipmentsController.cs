using Dprental.CatalogService.Application.DTOs;
using Dprental.CatalogService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/catalog/equipments")]
public sealed class EquipmentsController(ICatalogDiscoveryService discovery) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<EquipmentListItemDto>>> Search(
        [FromQuery] EquipmentListQuery query, CancellationToken cancellationToken)
    {
        SearchOutcome outcome = await discovery.SearchAsync(query, cancellationToken);
        if (!outcome.IsValid)
        {
            var errors = outcome.Errors
                .GroupBy(e => e.Field)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        return Ok(outcome.Page);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EquipmentDetailDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        EquipmentDetailDto? detail = await discovery.GetDetailAsync(id, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }
}