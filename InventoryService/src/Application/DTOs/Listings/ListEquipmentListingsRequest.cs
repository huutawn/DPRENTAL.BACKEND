using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu lấy danh sách EquipmentListing phân trang của người dùng hiện tại (My Listings).
/// Hỗ trợ lọc theo danh mục (EquipmentTypeId), thương hiệu (BrandId), trạng thái (Status) và tìm kiếm (Q).
/// </summary>
public sealed record ListEquipmentListingsRequest(
    int Page = 1,
    int PageSize = 20,
    string? Q = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    EquipmentListingStatus? Status = null,
    string? Sort = null
);
