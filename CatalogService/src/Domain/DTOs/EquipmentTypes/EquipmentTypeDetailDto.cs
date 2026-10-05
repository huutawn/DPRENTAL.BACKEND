using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    EquipmentTypeStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PublishedAt,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<EquipmentTypeMediaResponseDto> Media,
    int? CurrentSchemaVersion = null,
    EquipmentTypeSpecSchemaDto? SpecSchema = null,
    IReadOnlyList<EquipmentTypePrimarySpecDto>? PrimarySpecs = null)
{
    private readonly IReadOnlyList<EquipmentTypePrimarySpecDto> primarySpecs = PrimarySpecs ?? [];

    public IReadOnlyList<EquipmentTypePrimarySpecDto> PrimarySpecs
    {
        get => primarySpecs;
        init => primarySpecs = value ?? [];
    }

    public static IReadOnlyList<EquipmentTypePrimarySpecDto> ExtractPrimarySpecs(Entities.EquipmentTypeSpecDefinition? definition)
    {
        if (definition?.Fields is null || definition.Fields.Count == 0)
        {
            return [];
        }

        return definition.Fields
            .Where(f => f.Primary)
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new EquipmentTypePrimarySpecDto(f.Key, f.Label, f.Unit))
            .ToList();
    }
}
