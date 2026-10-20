namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu tìm kiếm và lọc danh sách tài sản của người dùng hiện tại (My Assets).
/// </summary>
public sealed record ListAssetsRequest(
    string? Q = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    bool? AcceptingBookings = null,
    int? ManufactureYearFrom = null,
    int? ManufactureYearTo = null,
    string? Sort = null,
    string? Filters = null,
    int Page = 1,
    int PageSize = 20);
