using CatalogService.Application.DTOs.Schemas;
using CatalogService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CatalogService.Api.Controllers;

[ApiController]
[Route("equipment-types/{equipmentTypeId:guid}/schema")]
public class EquipmentTypeSchemaController(ISchemaQueryService queries) : ControllerBase
{
    /// <summary>Schema của loại thiết bị đã công khai: bản active, hoặc bản cũ theo ?version=N</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SchemaVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchema(
        Guid equipmentTypeId, [FromQuery] int? version, CancellationToken cancellationToken)
    {
        if (version is < 1)
        {
            return BadRequest(new { Error = "version phải >= 1." });
        }

        SchemaVersionDto? dto = await queries.GetPublicSchemaAsync(equipmentTypeId, version, cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Admin: danh sách mọi version và Id để gọi các API drafts, publish, fields</summary>
    [HttpGet("versions")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(SchemaOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOverview(Guid equipmentTypeId, CancellationToken cancellationToken)
    {
        SchemaOverviewDto? dto = await queries.GetOverviewAsync(equipmentTypeId, cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Admin: đọc một version bất kỳ trạng thái (kể cả Deprecated)</summary>
    [HttpGet("versions/{versionNumber:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(SchemaVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVersion(
        Guid equipmentTypeId, int versionNumber, CancellationToken cancellationToken)
    {
        SchemaVersionDto? dto = await queries.GetVersionAsync(equipmentTypeId, versionNumber, cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }
}