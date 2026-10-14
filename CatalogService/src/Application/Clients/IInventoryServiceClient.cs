using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Clients;

/// <summary>
/// DTO chứa số lượng listing nhóm theo equipmentTypeId và brandId từ InventoryService.
/// </summary>
public sealed record InventoryListingStatisticsDto(
    IReadOnlyDictionary<Guid, int> ByEquipmentType,
    IReadOnlyDictionary<Guid, int> ByBrand
);

/// <summary>
/// HTTP client tích hợp với InventoryService để đọc thống kê số lượng listing.
/// </summary>
public interface IInventoryServiceClient
{
    /// <summary>
    /// Đọc thống kê số tin đăng theo scope (public | mine) và trạng thái tùy chọn.
    /// </summary>
    Task<Result<InventoryListingStatisticsDto>> GetListingStatisticsAsync(
        string scope,
        string? listingStatus,
        CancellationToken cancellationToken = default);
}
