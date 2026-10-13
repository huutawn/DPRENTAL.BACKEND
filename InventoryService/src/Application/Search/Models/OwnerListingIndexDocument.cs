namespace Dprental.InventoryService.Application.Search.Models;

/// <summary>
/// Projection document cho index danh mục EquipmentListing do chủ sở hữu quản lý trên Meilisearch.
/// </summary>
public sealed class OwnerListingIndexDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EquipmentTypeId { get; set; } = string.Empty;
    public string? BrandId { get; set; }
    public int Status { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
