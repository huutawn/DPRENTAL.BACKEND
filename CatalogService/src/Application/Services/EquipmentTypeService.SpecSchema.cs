using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Validation;

using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace Dprental.CatalogService.Application.Services;

public sealed partial class EquipmentTypeService
{
    public async Task<Result<IReadOnlyList<EquipmentTypeSpecSchemaDto>>> GetSpecSchemasAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        var entity = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        var schemas = await repository.GetSpecSchemasAsync(equipmentTypeId, cancellationToken);
        return Result.Success(schemas);
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> GetSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            // Caller public chỉ đọc schema published của type Published hoặc Disabled
            var result = await ResolvePublishedSchemaAsync(
                equipmentTypeId,
                version: version,
                allowDisabled: true,
                cancellationToken);

            if (result.IsFailure)
            {
                // Giữ nguyên mapping lỗi cũ: không tìm thấy schema trả NotFound thay vì SchemaNotFound
                if (result.Error.Code == CatalogErrorCode.EquipmentType.SchemaNotFound)
                {
                    return CatalogErrors.EquipmentType.NotFound;
                }

                return result.Error;
            }

            return result;
        }

        var entity = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        var schema = await repository.GetSpecSchemaAsync(equipmentTypeId, version, cancellationToken);
        if (schema is null)
        {
            return CatalogErrors.EquipmentType.SchemaNotFound;
        }

        return Result.Success(schema);
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> CreateSpecSchemaDraftAsync(
        Guid equipmentTypeId,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var validationResult = SpecSchemaDefinitionValidator.Validate(definition);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var parent = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (parent is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        var existingDraft = await repository.GetDraftSpecSchemaEntityAsync(equipmentTypeId, cancellationToken);
        if (existingDraft is not null)
        {
            return CatalogErrors.EquipmentType.DraftAlreadyExists;
        }

        return await repository.AddSpecSchemaDraftAsync(parent, definition, cancellationToken);
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> UpdateSpecSchemaDraftAsync(
        Guid equipmentTypeId,
        int version,
        EquipmentTypeSpecDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var validationResult = SpecSchemaDefinitionValidator.Validate(definition);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var parent = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (parent is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        var schema = await repository.GetSpecSchemaEntityAsync(equipmentTypeId, version, cancellationToken);
        if (schema is null)
        {
            return CatalogErrors.EquipmentType.SchemaNotFound;
        }

        if (schema.PublishedAt.HasValue)
        {
            return CatalogErrors.EquipmentType.SchemaAlreadyPublished;
        }

        return await repository.UpdateSpecSchemaDraftAsync(parent, schema, definition, cancellationToken);
    }

    public async Task<Result<EquipmentTypeSpecSchemaDto>> PublishSpecSchemaAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var parent = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (parent is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        var schema = await repository.GetSpecSchemaEntityAsync(equipmentTypeId, version, cancellationToken);
        if (schema is null)
        {
            return CatalogErrors.EquipmentType.SchemaNotFound;
        }

        if (schema.PublishedAt.HasValue)
        {
            return CatalogErrors.EquipmentType.SchemaAlreadyPublished;
        }

        if (schema.Version <= (parent.CurrentSchemaVersion ?? 0))
        {
            return CatalogErrors.EquipmentType.InvalidSchemaVersion;
        }

        var validationResult = SpecSchemaDefinitionValidator.Validate(schema.Definition);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        return await repository.PublishSpecSchemaAsync(parent, schema, cancellationToken);
    }

    public async Task<Result<EquipmentTypePublishedSchemaResponse>> GetCurrentPublishedSchemaAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        var resolveResult = await ResolvePublishedSchemaAsync(
            equipmentTypeId,
            version: null,
            allowDisabled: false,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        var schema = resolveResult.Value;
        var sortedFields = schema.Definition.Fields
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();

        return new EquipmentTypePublishedSchemaResponse(
            equipmentTypeId,
            schema.Version,
            sortedFields);
    }

    public async Task<Result<EquipmentTypePublishedSchemaResponse>> GetPublishedSchemaByVersionAsync(
        Guid equipmentTypeId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var resolveResult = await ResolvePublishedSchemaAsync(
            equipmentTypeId,
            version: version,
            allowDisabled: true,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        var schema = resolveResult.Value;
        var sortedFields = schema.Definition.Fields
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();

        return new EquipmentTypePublishedSchemaResponse(
            equipmentTypeId,
            schema.Version,
            sortedFields);
    }

    public async Task<Result<SpecificationValidationResult>> ValidateSpecificationAsync(
        Guid equipmentTypeId,
        ValidateSpecificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SchemaVersion <= 0 || request.SpecValues.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return CatalogErrors.Catalog.InvalidRequest;
        }

        var resolveResult = await ResolvePublishedSchemaAsync(
            equipmentTypeId,
            version: request.SchemaVersion,
            allowDisabled: false,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            return resolveResult.Error;
        }

        var schema = resolveResult.Value;
        var validationResult = SpecificationValuesValidator.Validate(schema.Definition, request.SpecValues);
        return Result.Success(validationResult);
    }

    private async Task<Result<EquipmentTypeSpecSchemaDto>> ResolvePublishedSchemaAsync(
        Guid equipmentTypeId,
        int? version,
        bool allowDisabled,
        CancellationToken cancellationToken)
    {
        var metadata = await repository.GetMetadataByIdAsync(equipmentTypeId, cancellationToken);
        if (metadata is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (allowDisabled)
        {
            if (metadata.Status == EquipmentTypeStatus.Draft)
            {
                return CatalogErrors.EquipmentType.NotFound;
            }
        }
        else
        {
            if (metadata.Status != EquipmentTypeStatus.Published)
            {
                return CatalogErrors.EquipmentType.NotFound;
            }
        }

        int targetVersion;
        if (version.HasValue)
        {
            if (version.Value <= 0)
            {
                return CatalogErrors.EquipmentType.SchemaNotFound;
            }
            targetVersion = version.Value;
        }
        else
        {
            if (!metadata.CurrentSchemaVersion.HasValue)
            {
                return CatalogErrors.EquipmentType.SchemaNotFound;
            }
            targetVersion = metadata.CurrentSchemaVersion.Value;
        }

        EquipmentTypeSpecSchemaDto? schema;
        try
        {
            schema = await repository.GetSpecSchemaAsync(
                equipmentTypeId,
                targetVersion,
                publishedOnly: true,
                cancellationToken);
        }
        catch (JsonException ex)
        {
            LogSchemaCorrupted(logger, equipmentTypeId, targetVersion, ex.Message);
            return CatalogErrors.Catalog.Uncategorized;
        }

        if (schema is null)
        {
            return CatalogErrors.EquipmentType.SchemaNotFound;
        }

        var definitionValidation = SpecSchemaDefinitionValidator.Validate(schema.Definition);
        if (definitionValidation.IsFailure)
        {
            LogSchemaCorrupted(logger, equipmentTypeId, targetVersion, definitionValidation.Error.Message);
            return CatalogErrors.Catalog.Uncategorized;
        }

        return Result.Success(schema);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Specification schema definition corrupted. EquipmentTypeId: {EquipmentTypeId}, Version: {Version}, Detail: {Detail}")]
    private static partial void LogSchemaCorrupted(ILogger logger, Guid equipmentTypeId, int version, string detail);
}
