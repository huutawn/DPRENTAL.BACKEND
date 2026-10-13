namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Yêu cầu tìm kiếm tập ứng viên cho danh mục Listing công khai.
/// Giới hạn số lượng ứng viên do CandidateLimit trong cấu hình quản lý duy nhất.
/// </summary>
public sealed record PublicListingSearchIndexQuery(
    string Keyword,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null
);

/// <summary>
/// Yêu cầu tìm kiếm tập ứng viên cho danh mục Listing của chủ sở hữu.
/// </summary>
public sealed record OwnerListingSearchIndexQuery(
    Guid OwnerUserId,
    string Keyword,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    int? Status = null
);

/// <summary>
/// Yêu cầu tìm kiếm tập ứng viên cho danh mục Tài sản vật lý của chủ sở hữu.
/// </summary>
public sealed record OwnerAssetSearchIndexQuery(
    Guid OwnerUserId,
    string Keyword,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    bool? AcceptingBookings = null,
    int? ManufactureYearFrom = null,
    int? ManufactureYearTo = null
);
