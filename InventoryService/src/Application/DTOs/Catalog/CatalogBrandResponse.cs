namespace Dprental.InventoryService.Application.DTOs.Catalog;

/// <summary>
/// Thông tin thương hiệu chuẩn từ CatalogService.
/// </summary>
public sealed record CatalogBrandResponse(
    Guid Id,
    string Code,
    string Name,
    string Status
);
