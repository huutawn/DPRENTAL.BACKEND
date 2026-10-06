namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu tìm kiếm và lọc danh sách tài sản công khai (Public Asset Discovery).
/// Chỉ bao gồm các tham số công khai được phép theo whitelist.
/// </summary>
public sealed record ListPublicAssetsRequest(
    string? Q = null,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    int? ManufactureYearFrom = null,
    int? ManufactureYearTo = null,
    string? Sort = null,
    string? Filters = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Page = 1,
    int PageSize = 20);
