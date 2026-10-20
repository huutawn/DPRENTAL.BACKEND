using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Bản ghi tối giản trích xuất từ EquipmentListing phục vụ API batch cho Rental.
/// </summary>
public sealed record EquipmentListingBatchItem(
    Guid Id,
    Guid OwnerUserId,
    Guid EquipmentTypeId,
    string Name,
    Guid? ThumbnailMediaId,
    decimal DailyRate,
    string Currency,
    EquipmentListingStatus Status,
    decimal DepositAmount,
    EquipmentListingBatchWarehouse? Warehouse = null
);

public sealed record EquipmentListingBatchWarehouse(
    string? AddressDetail,
    string? ProvinceCode,
    string? ProvinceName,
    string? WardCode,
    string? WardName,
    double? Latitude,
    double? Longitude
);
