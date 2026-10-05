using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Validation;

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
        var entity = await repository.GetByIdAsync(equipmentTypeId, cancellationToken);
        if (entity is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (!caller.IsAdmin)
        {
            // Caller public chỉ đọc schema published của type Published hoặc Disabled
            if (entity.Status == EquipmentTypeStatus.Draft)
            {
                return CatalogErrors.EquipmentType.NotFound;
            }

            var publishedSchema = await repository.GetSpecSchemaAsync(equipmentTypeId, version, cancellationToken);
            if (publishedSchema is null || publishedSchema.PublishedAt is null)
            {
                return CatalogErrors.EquipmentType.NotFound;
            }

            return Result.Success(publishedSchema);
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
}
