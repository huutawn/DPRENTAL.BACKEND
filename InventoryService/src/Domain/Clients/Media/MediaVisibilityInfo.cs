namespace Dprental.InventoryService.Domain.Clients.Media;

public sealed record MediaVisibilityInfo(
    Guid MediaId,
    string Visibility);
