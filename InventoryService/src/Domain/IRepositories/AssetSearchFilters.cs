namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Tùy chọn sắp xếp cho danh sách tài sản theo whitelist.
/// </summary>
public enum AssetSortOption
{
    CreatedDesc = 0,
    CreatedAsc = 1,
    ModelDesc = 2,
    ModelAsc = 3,
    ManufactureYearDesc = 4,
    ManufactureYearAsc = 5
}

/// <summary>
/// Một điều kiện lọc động JSONB đã được validate theo schema tương thích.
/// </summary>
public sealed record AssetDynamicFilterCriterion(
    string Field,
    string Operator,
    object Value,
    string DataType);

/// <summary>
/// Các bộ lọc tìm kiếm tài sản dùng chung không gắn với phạm vi người dùng (scope-free).
/// </summary>
public sealed record AssetSearchFilters(
    string? SearchKeyword = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    int? ManufactureYearFrom = null,
    int? ManufactureYearTo = null,
    AssetSortOption Sort = AssetSortOption.CreatedDesc,
    IReadOnlyList<int>? CompatibleSchemaVersions = null,
    IReadOnlyList<AssetDynamicFilterCriterion>? DynamicFilters = null);
