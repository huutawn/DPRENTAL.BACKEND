namespace Dprental.InventoryService.Domain.Clients.Catalog;

public sealed record CatalogBrandResponse(
    Guid Id,
    string Code,
    string Name,
    bool IsActive);
