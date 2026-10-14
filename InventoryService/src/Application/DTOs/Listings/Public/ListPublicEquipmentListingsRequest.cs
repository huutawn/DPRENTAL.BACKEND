namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu tìm kiếm và khám phá danh mục EquipmentListing công khai.
/// </summary>
public sealed record ListPublicEquipmentListingsRequest(
    string? Q = null,
    Guid? CategoryId = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    string? Sort = null,
    string? Filters = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int? RequestedQuantity = null,
    int Page = 1,
    int PageSize = 20
);
