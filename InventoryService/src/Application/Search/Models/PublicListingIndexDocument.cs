namespace Dprental.InventoryService.Application.Search.Models;

/// <summary>
/// Projection document cho index danh mục EquipmentListing công khai trên Meilisearch.
/// BẢO MẬT: Tuyệt đối không chứa OwnerUserId, SerialNumber, AssetCode hoặc AssetId.
/// </summary>
public sealed class PublicListingIndexDocument
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EquipmentTypeId { get; set; } = string.Empty;
    public string? BrandId { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
