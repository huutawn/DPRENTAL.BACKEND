namespace Dprental.InventoryService.Application.Search.Configuration;

/// <summary>
/// Định nghĩa cấu hình chỉ mục (searchable, filterable, sortable, typo tolerance) dùng chung duy nhất.
/// </summary>
public static class SearchIndexDefinition
{
    // Công khai chỉ tìm tên/model; serialNumber chỉ xuất hiện ở chỉ mục asset dành cho chủ sở hữu.
    public static readonly string[] PublicSearchableAttributes = ["name", "model"];
    public static readonly string[] PublicFilterableAttributes = ["equipmentTypeId", "brandId"];
    public static readonly string[] PublicSortableAttributes = ["createdAt", "model"];

    public static readonly string[] OwnerListingSearchableAttributes = ["name", "model"];
    public static readonly string[] OwnerListingFilterableAttributes = ["ownerUserId", "equipmentTypeId", "brandId", "status"];
    public static readonly string[] OwnerListingSortableAttributes = ["createdAt", "model"];

    public static readonly string[] OwnerAssetSearchableAttributes = ["name", "model", "serialNumber"];
    public static readonly string[] OwnerAssetFilterableAttributes = ["ownerUserId", "equipmentTypeId", "brandId", "acceptingBookings", "manufactureYear"];
    public static readonly string[] OwnerAssetSortableAttributes = ["createdAt", "model"];
    // Giữ dấu nối/gạch dưới trong mã; không sửa lỗi chính tả serial để tránh tìm nhầm thiết bị.
    public static readonly string[] OwnerAssetNonSeparatorTokens = ["-", "_"];
    public static readonly string[] OwnerAssetDisabledTypoAttributes = ["serialNumber"];
}
