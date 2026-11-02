
namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Kết quả đọc danh sách EquipmentListing theo lô (batch) phục vụ Rental Service.
/// </summary>
public sealed record EquipmentListingBatchResponse(
    IReadOnlyList<EquipmentListingBatchItemResponse> Items,
    IReadOnlyList<Guid> MissingIds
);
