using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record EquipmentTypeMetadataDto(
    Guid Id,
    EquipmentTypeStatus Status,
    int? CurrentSchemaVersion);
