using CatalogService.Application.DTOs.Schemas;
using CatalogService.Application.IServices;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CatalogService.Api.Controllers;

[ApiController]
[Route("schemas")] // Swagger sẽ hiển thị dạng: /api/v1/catalog/schemas
[Authorize(Roles = "Admin")] // Chặn truy cập từ người dùng thông thường
public class SchemaController : ControllerBase
{
    private readonly ISchemaService _schemaService;

    public SchemaController(ISchemaService schemaService)
    {
        _schemaService = schemaService;
    }

    /// <summary>
    /// Tạo một phiên bản Draft mới (v2) từ Schema hiện tại
    /// </summary>
    [HttpPost("{schemaId}/drafts")]
    public async Task<IActionResult> CreateDraftVersion(Guid schemaId)
    {
        try
        {
            var newDraft = await _schemaService.CreateNewDraftVersionAsync(schemaId);
            return Ok(newDraft);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Phát hành (Publish) bản Draft thành bản chính thức
    /// </summary>
    [HttpPost("versions/{versionId}/publish")]
    public async Task<IActionResult> PublishVersion(Guid versionId)
    {
        try
        {
            await _schemaService.PublishSchemaVersionAsync(versionId);
            return Ok(new { Message = "Publish Schema Version thành công!" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
    }
    /// <summary>Thêm một Field vào version đang Draft</summary>
    [HttpPost("versions/{versionId:guid}/fields")]
    [ProducesResponseType(typeof(SpecFieldDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddSpecField(
        Guid versionId, [FromBody] AddSpecFieldRequest request, CancellationToken cancellationToken)
    {
        try
        {
            SpecFieldDto field = await _schemaService.AddFieldAsync(versionId, request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, field);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
    }

    /// <summary>Xóa một Field khỏi version đang Draft</summary>
    [HttpDelete("versions/{versionId:guid}/fields/{fieldId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveSpecField(
        Guid versionId, Guid fieldId, CancellationToken cancellationToken)
    {
        try
        {
            await _schemaService.RemoveFieldAsync(versionId, fieldId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Cập nhật thông số của một Field (Chỉ cho phép nếu Version đang là Draft)
    /// </summary>
    [HttpPut("versions/{versionId}/fields/{fieldId}")]
    public async Task<IActionResult> UpdateSpecField(Guid versionId, Guid fieldId, [FromBody] SpecField updateData)
    {
        try
        {
            await _schemaService.UpdateSpecFieldAsync(versionId, fieldId, updateData);
            return Ok(new { Message = "Cập nhật SpecField thành công!" });
        }
        catch (InvalidOperationException ex)
        {
            // Trả về lỗi 400 nếu cố tình sửa bản đã Published
            return BadRequest(new { Error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
    }
}