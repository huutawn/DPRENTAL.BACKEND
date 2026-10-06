using CatalogService.Application.DTOs.Schemas;
using CatalogService.Application.IServices;
using CatalogService.Application.Mapping;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Dprental.CatalogService.Domain.ValueObjects;

namespace CatalogService.Application.Services;

public sealed class SchemaQueryService(IEquipmentTypeSchemaRepository repository) : ISchemaQueryService
{
    public async Task<SchemaOverviewDto?> GetOverviewAsync(
        Guid equipmentTypeId, CancellationToken cancellationToken = default)
    {
        EquipmentType? type = await repository.GetWithSchemaReadOnlyAsync(equipmentTypeId, cancellationToken);
        if (type?.Schema is null)
        {
            return null;
        }

        return new SchemaOverviewDto(
            type.Id,
            type.Schema.Id,
            type.ActiveSchemaVersionId,
            type.Schema.Versions
                .OrderBy(version => version.VersionNumber)
                .Select(version => SchemaVersionMapper.ToSummary(version, type.ActiveSchemaVersionId))
                .ToList());
    }

    public async Task<SchemaVersionDto?> GetVersionAsync(
        Guid equipmentTypeId, int versionNumber, CancellationToken cancellationToken = default)
    {
        EquipmentType? type = await repository.GetWithSchemaReadOnlyAsync(equipmentTypeId, cancellationToken);
        SchemaVersion? version = type?.Schema?.Versions.SingleOrDefault(item => item.VersionNumber == versionNumber);
        return version is null ? null : SchemaVersionMapper.ToDto(version, type!.ActiveSchemaVersionId);
    }

    public async Task<SchemaVersionDto?> GetPublicSchemaAsync(
        Guid equipmentTypeId, int? versionNumber, CancellationToken cancellationToken = default)
    {
        EquipmentType? type = await repository.GetWithSchemaReadOnlyAsync(equipmentTypeId, cancellationToken);
        if (type?.Schema is null || type.Status != EquipmentTypeStatus.Published)
        {
            return null;
        }

        SchemaVersion? version = versionNumber is int number
            ? type.Schema.Versions.SingleOrDefault(item => item.VersionNumber == number)
            : type.Schema.Versions.SingleOrDefault(item => item.Id == type.ActiveSchemaVersionId);

        // Draft không bao giờ lộ ra public
        return version is null or { Status: SchemaVersionStatus.Draft }
            ? null
            : SchemaVersionMapper.ToDto(version, type.ActiveSchemaVersionId);
    }
}