using CatalogService.Application.DTOs.Schemas;
using CatalogService.Application.IServices;
using CatalogService.Application.Mapping;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.IRepositories;

namespace CatalogService.Application.Services;

/// <summary>Compatibility queries backed by the single versioned JSON schema model.</summary>
public sealed class SchemaQueryService(IEquipmentTypeRepository repository) : ISchemaQueryService
{
    public async Task<SchemaOverviewDto?> GetOverviewAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeMetadataDto? metadata = await repository.GetMetadataByIdAsync(equipmentTypeId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        IReadOnlyList<EquipmentTypeSpecSchemaDto> schemas =
            await repository.GetSpecSchemasAsync(equipmentTypeId, cancellationToken);
        return new SchemaOverviewDto(
            equipmentTypeId,
            equipmentTypeId,
            schemas.FirstOrDefault(s => s.Version == metadata.CurrentSchemaVersion)?.Id,
            schemas.Select(s => SchemaCompatibilityMapper.ToSummary(s, metadata.CurrentSchemaVersion)).ToList());
    }

    public async Task<SchemaVersionDto?> GetVersionAsync(
        Guid equipmentTypeId,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeMetadataDto? metadata = await repository.GetMetadataByIdAsync(equipmentTypeId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        EquipmentTypeSpecSchemaDto? schema = await repository.GetSpecSchemaAsync(
            equipmentTypeId, versionNumber, publishedOnly: false, cancellationToken);
        return schema is null ? null : SchemaCompatibilityMapper.ToDto(schema, metadata.CurrentSchemaVersion);
    }

    public async Task<SchemaVersionDto?> GetPublicSchemaAsync(
        Guid equipmentTypeId,
        int? versionNumber,
        CancellationToken cancellationToken = default)
    {
        EquipmentTypeMetadataDto? metadata = await repository.GetMetadataByIdAsync(equipmentTypeId, cancellationToken);
        if (metadata is null || metadata.Status != EquipmentTypeStatus.Published)
        {
            return null;
        }

        int? version = versionNumber ?? metadata.CurrentSchemaVersion;
        if (!version.HasValue)
        {
            return null;
        }

        EquipmentTypeSpecSchemaDto? schema = await repository.GetSpecSchemaAsync(
            equipmentTypeId, version.Value, publishedOnly: true, cancellationToken);
        return schema is null ? null : SchemaCompatibilityMapper.ToDto(schema, metadata.CurrentSchemaVersion);
    }
}
