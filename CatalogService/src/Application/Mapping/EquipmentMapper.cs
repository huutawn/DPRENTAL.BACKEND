using System.Text.Json;
using Dprental.CatalogService.Application.DTOs;
using Dprental.CatalogService.Domain.ValueObjects;

namespace Dprental.CatalogService.Application.Mapping;

public static class EquipmentMapper
{
    public static EquipmentListItemDto ToDto(EquipmentListItem item) =>
        new(item.Id, item.Name, item.Brand, item.DailyPrice, item.RegionId, item.ThumbnailMediaId,
            new CategoryDto(item.Category.Id, item.Category.Name));

    public static EquipmentDetailDto ToDto(EquipmentDetail detail) =>
        new(detail.Id, detail.Code, detail.Name, detail.Brand, detail.Description, detail.DailyPrice,
            detail.RegionId, new CategoryDto(detail.Category.Id, detail.Category.Name),
            ParseJson(detail.SpecificationsJson), detail.MediaIds);

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}