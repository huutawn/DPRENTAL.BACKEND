namespace Dprental.InventoryService.Application.DTOs.Catalog;

/// <summary>
/// Thông tin chi tiết loại thiết bị từ CatalogService.
/// </summary>
public sealed record CatalogEquipmentTypeResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    int Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PublishedAt
);
