namespace Dprental.InventoryService.Application.Search.Models;

/// <summary>
/// Projection document cho index danh mục thiết bị vật lý cá nhân (InventoryAsset) trên Meilisearch.
/// SerialNumber và Model là các trường tìm kiếm chính; SerialNumber tắt typo tolerance.
/// </summary>
public sealed class OwnerAssetIndexDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string EquipmentListingId { get; set; } = string.Empty;
    public string? SerialNumber { get; set; }
    public string Model { get; set; } = string.Empty;
    public string EquipmentTypeId { get; set; } = string.Empty;
    public string? BrandId { get; set; }
    public int? ManufactureYear { get; set; }
    public bool AcceptingBookings { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
