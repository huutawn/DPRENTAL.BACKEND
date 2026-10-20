namespace Dprental.InventoryService.Application.DTOs.Catalog;

public sealed record CatalogBrandResponse(
    Guid Id,
    string Code,
    string Name,
    bool IsActive);
