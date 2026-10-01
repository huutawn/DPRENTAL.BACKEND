using System.Text.Json;

namespace Dprental.CatalogService.Domain.DTOs.Equipments;

public sealed record EquipmentDetailDto(
    Guid Id,
    string Code,
    string Name,
    string? Brand,
    string? Description,
    decimal DailyPrice,
    Guid RegionId,
    CategoryDto Category,
    JsonElement? Specifications,
    IReadOnlyList<Guid> MediaIds);
