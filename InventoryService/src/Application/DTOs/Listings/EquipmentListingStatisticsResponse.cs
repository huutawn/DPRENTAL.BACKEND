namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thống kê số lượng listing nhóm theo equipmentTypeId và brandId.
/// </summary>
public sealed record EquipmentListingStatisticsResponse(
    IReadOnlyDictionary<Guid, int> ByEquipmentType,
    IReadOnlyDictionary<Guid, int> ByBrand
);
