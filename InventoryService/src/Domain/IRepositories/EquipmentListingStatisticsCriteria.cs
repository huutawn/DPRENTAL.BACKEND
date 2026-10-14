using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Tiêu chí tổng hợp thống kê số lượng listing theo equipmentTypeId và brandId.
/// </summary>
public sealed record EquipmentListingStatisticsCriteria(
    Guid? OwnerUserId = null,
    EquipmentListingStatus? Status = null
);

/// <summary>
/// Kết quả tổng hợp thống kê số lượng listing từ repository.
/// </summary>
public sealed record EquipmentListingStatisticsResult(
    IReadOnlyDictionary<Guid, int> ByEquipmentType,
    IReadOnlyDictionary<Guid, int> ByBrand
);
