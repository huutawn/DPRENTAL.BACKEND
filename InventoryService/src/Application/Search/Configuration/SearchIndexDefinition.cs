namespace Dprental.InventoryService.Application.Search.Configuration;

/// <summary>
/// Định nghĩa cấu hình chỉ mục (searchable, filterable, sortable, typo tolerance) dùng chung duy nhất.
/// </summary>
public static class SearchIndexDefinition
{
    public static readonly string[] PublicSearchableAttributes = ["name", "model", "description"];
    public static readonly string[] PublicFilterableAttributes = ["equipmentTypeId", "brandId"];
    public static readonly string[] PublicSortableAttributes = ["createdAt", "model"];

    public static readonly string[] OwnerListingSearchableAttributes = ["name", "model", "description"];
    public static readonly string[] OwnerListingFilterableAttributes = ["ownerUserId", "equipmentTypeId", "brandId", "status"];
    public static readonly string[] OwnerListingSortableAttributes = ["createdAt", "model"];

    public static readonly string[] OwnerAssetSearchableAttributes = ["serialNumber", "model"];
    public static readonly string[] OwnerAssetFilterableAttributes = ["ownerUserId", "equipmentTypeId", "brandId", "acceptingBookings", "manufactureYear"];
    public static readonly string[] OwnerAssetSortableAttributes = ["createdAt", "model"];
    public static readonly string[] OwnerAssetNonSeparatorTokens = ["-", "_"];
    public static readonly string[] OwnerAssetDisabledTypoAttributes = ["serialNumber"];
}
