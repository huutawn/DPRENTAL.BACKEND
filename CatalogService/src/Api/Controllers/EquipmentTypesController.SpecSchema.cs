using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Security;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Controllers;

public sealed partial class EquipmentTypesController
{
    /// <summary>Lấy toàn bộ các phiên bản specification schema của loại thiết bị (chỉ dành cho Admin).</summary>
    [HttpGet("{id:guid}/spec-schemas")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<IReadOnlyList<EquipmentTypeSpecSchemaDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<EquipmentTypeSpecSchemaDto>>> GetSpecSchemas(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<EquipmentTypeSpecSchemaDto>> result = await service.GetSpecSchemasAsync(id, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy chi tiết một phiên bản specification schema theo version.</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/spec-schemas/{version:int}")]
    [ProducesResponseType<EquipmentTypeSpecSchemaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquipmentTypeSpecSchemaDto>> GetSpecSchemaByVersion(
        Guid id,
        int version,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeSpecSchemaDto> result = await service.GetSpecSchemaAsync(id, version, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Tạo mới bản nháp specification schema tiếp theo cho loại thiết bị (chỉ dành cho Admin).</summary>
    [HttpPost("{id:guid}/spec-schemas")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeSpecSchemaDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSpecSchemaDraft(
        Guid id,
        [FromBody] EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeSpecSchemaDto> result = await service.CreateSpecSchemaDraftAsync(id, definition, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Thay đổi toàn bộ definition của bản nháp specification schema (chỉ dành cho Admin).</summary>
    [HttpPut("{id:guid}/spec-schemas/{version:int}")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeSpecSchemaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSpecSchemaDraft(
        Guid id,
        int version,
        [FromBody] EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeSpecSchemaDto> result = await service.UpdateSpecSchemaDraftAsync(id, version, definition, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Publish bản nháp specification schema và cập nhật current version (chỉ dành cho Admin).</summary>
    [HttpPost("{id:guid}/spec-schemas/{version:int}/publish")]
    [Authorize(Policy = CatalogPolicies.EquipmentManage)]
    [ProducesResponseType<EquipmentTypeSpecSchemaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PublishSpecSchema(
        Guid id,
        int version,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeSpecSchemaDto> result = await service.PublishSpecSchemaAsync(id, version, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy specification schema hiện tại của loại thiết bị đã công khai.</summary>
    [AllowAnonymous]
    [HttpGet("{equipmentTypeId:guid}/schema")]
    [ProducesResponseType<EquipmentTypePublishedSchemaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EquipmentTypePublishedSchemaResponse>> GetCurrentSchema(
        Guid equipmentTypeId,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypePublishedSchemaResponse> result = await service.GetCurrentPublishedSchemaAsync(equipmentTypeId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy một phiên bản specification schema đã publish của loại thiết bị (Published hoặc Disabled).</summary>
    [AllowAnonymous]
    [HttpGet("{equipmentTypeId:guid}/schemas/{version:int}")]
    [ProducesResponseType<EquipmentTypePublishedSchemaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EquipmentTypePublishedSchemaResponse>> GetSchemaByVersion(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypePublishedSchemaResponse> result = await service.GetPublishedSchemaByVersionAsync(equipmentTypeId, version, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Lấy thông tin specification schemas theo batch (chỉ đọc, gồm current schema và các published version được yêu cầu).</summary>
    [AllowAnonymous]
    [HttpPost("{id:guid}/schemas/batch")]
    [ProducesResponseType<EquipmentTypeBatchSchemasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EquipmentTypeBatchSchemasResponse>> GetSchemasBatch(
        Guid id,
        [FromBody] EquipmentTypeBatchSchemasRequest request,
        CancellationToken cancellationToken)
    {
        Result<EquipmentTypeBatchSchemasResponse> result = await service.GetSchemasBatchAsync(id, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Kiểm tra tính hợp lệ của specification values canonical theo schema version đã publish.</summary>
    [Authorize]
    [HttpPost("{equipmentTypeId:guid}/specifications/validate")]
    [ProducesResponseType<SpecificationValidationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<SpecificationValidationResult>> ValidateSpecification(
        Guid equipmentTypeId,
        [FromBody] ValidateSpecificationRequest request,
        CancellationToken cancellationToken)
    {
        Result<SpecificationValidationResult> result = await service.ValidateSpecificationAsync(equipmentTypeId, request, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
