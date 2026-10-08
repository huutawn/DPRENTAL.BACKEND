namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu lấy danh sách EquipmentListing phân trang của người dùng hiện tại (My Listings).
/// </summary>
public sealed record ListEquipmentListingsRequest(
    int Page = 1,
    int PageSize = 20
);
