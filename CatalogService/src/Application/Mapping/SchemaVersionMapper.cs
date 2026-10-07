using CatalogService.Application.DTOs.Schemas;
using Dprental.CatalogService.Domain.Entities;

namespace CatalogService.Application.Mapping;

public static class SchemaVersionMapper
{
    public static SchemaVersionDto ToDto(SchemaVersion version, Guid? activeVersionId) =>
        new(
            version.Id,
            version.SpecSchemaId,
            version.VersionNumber,
            version.Status.ToString(),
            version.Id == activeVersionId,
            version.CreatedAt,
            version.PublishedAt,
            version.DeprecatedAt,
            version.SpecFields
                .OrderBy(field => field.DisplayOrder)
                .ThenBy(field => field.Id)
                .Select(ToFieldDto)
                .ToList());

    public static SchemaVersionSummaryDto ToSummary(SchemaVersion version, Guid? activeVersionId) =>
        new(
            version.Id,
            version.VersionNumber,
            version.Status.ToString(),
            version.Id == activeVersionId,
            version.CreatedAt,
            version.PublishedAt,
            version.DeprecatedAt,
            version.SpecFields.Count);

    public static SpecFieldDto ToFieldDto(SpecField field) =>
        new(
            field.Id,
            field.Key,
            field.Label,
            SpecDataTypeMapper.ToContract(field.DataType),
            field.Unit,
            field.IsRequired,
            field.MinValue,
            field.MaxValue,
            field.DisplayOrder,
            field.Options
                .OrderBy(option => option.Label, StringComparer.Ordinal)
                .Select(option => new SpecOptionDto(option.Id, option.Value, option.Label))
                .ToList());
}