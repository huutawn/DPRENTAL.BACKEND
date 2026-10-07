using System.Security.Cryptography;
using System.Text;
using CatalogService.Application.DTOs.Schemas;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;

namespace CatalogService.Application.Mapping;

/// <summary>Adapts the original field based schema API to the versioned JSON definition.</summary>
internal static class SchemaCompatibilityMapper
{
    public static SchemaVersionDto ToDto(
        EquipmentTypeSpecSchema schema,
        int? currentVersion) => new(
            schema.Id,
            schema.EquipmentTypeId,
            schema.Version,
            schema.PublishedAt is null ? "Draft" : "Published",
            schema.Version == currentVersion,
            schema.CreatedAt,
            schema.PublishedAt,
            null,
            schema.Definition.Fields
                .OrderBy(f => f.DisplayOrder)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => ToFieldDto(schema.Id, f))
                .ToList());

    public static SchemaVersionDto ToDto(
        EquipmentTypeSpecSchemaDto schema,
        int? currentVersion) => new(
            schema.Id,
            schema.EquipmentTypeId,
            schema.Version,
            schema.Status,
            schema.Version == currentVersion,
            schema.CreatedAt,
            schema.PublishedAt,
            null,
            schema.Definition.Fields
                .OrderBy(f => f.DisplayOrder)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => ToFieldDto(schema.Id, f))
                .ToList());

    public static SchemaVersionSummaryDto ToSummary(
        EquipmentTypeSpecSchema schema,
        int? currentVersion) => new(
            schema.Id,
            schema.Version,
            schema.PublishedAt is null ? "Draft" : "Published",
            schema.Version == currentVersion,
            schema.CreatedAt,
            schema.PublishedAt,
            null,
            schema.Definition.Fields.Count);

    public static SchemaVersionSummaryDto ToSummary(
        EquipmentTypeSpecSchemaDto schema,
        int? currentVersion) => new(
            schema.Id,
            schema.Version,
            schema.Status,
            schema.Version == currentVersion,
            schema.CreatedAt,
            schema.PublishedAt,
            null,
            schema.Definition.Fields.Count);

    public static SpecFieldDto ToFieldDto(Guid schemaId, EquipmentTypeSpecField field) => new(
        StableId(schemaId, field.Key),
        field.Key,
        field.Label,
        field.DataType,
        field.Unit,
        field.Required,
        field.Min,
        field.Max,
        field.DisplayOrder,
        (field.Options ?? Array.Empty<EquipmentTypeSpecOption>())
            .OrderBy(o => o.Label, StringComparer.Ordinal)
            .Select(o => new SpecOptionDto(StableId(schemaId, field.Key + ":" + o.Value), o.Value, o.Label))
            .ToList());

    public static Guid StableId(Guid schemaId, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{schemaId:N}:{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
