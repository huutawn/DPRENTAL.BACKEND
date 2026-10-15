using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Bản ghi tối giản trích xuất từ EquipmentListing phục vụ API batch cho Rental.
/// </summary>
public sealed record EquipmentListingBatchItem(
    Guid Id,
    Guid OwnerUserId,
    string Name,
    Guid? ThumbnailMediaId,
    decimal DailyRate,
    string Currency,
    EquipmentListingStatus Status
);
