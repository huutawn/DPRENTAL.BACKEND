using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EquipmentTypeMediaItemDto(
    Guid MediaId,
    int DisplayOrder,
    bool IsThumbnail);
