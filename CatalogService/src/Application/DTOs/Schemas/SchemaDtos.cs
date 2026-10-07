namespace CatalogService.Application.DTOs.Schemas;

public sealed record SpecOptionDto(Guid Id, string Value, string Label);

public sealed record SpecFieldDto(
    Guid Id, string Key, string Label, string DataType, string? Unit, bool IsRequired,
    decimal? MinValue, decimal? MaxValue, int DisplayOrder, IReadOnlyList<SpecOptionDto> Options);

public sealed record SchemaVersionDto(
    Guid Id, Guid SpecSchemaId, int VersionNumber, string Status, bool IsActive,
    DateTime CreatedAt, DateTime? PublishedAt, DateTime? DeprecatedAt, IReadOnlyList<SpecFieldDto> Fields);

public sealed record SchemaVersionSummaryDto(
    Guid Id, int VersionNumber, string Status, bool IsActive,
    DateTime CreatedAt, DateTime? PublishedAt, DateTime? DeprecatedAt, int FieldCount);

/// <summary>Admin dùng để lấy SpecSchemaId và VersionId cho các API tạo draft, publish, sửa field.</summary>
public sealed record SchemaOverviewDto(
    Guid EquipmentTypeId, Guid SpecSchemaId, Guid? ActiveSchemaVersionId,
    IReadOnlyList<SchemaVersionSummaryDto> Versions);