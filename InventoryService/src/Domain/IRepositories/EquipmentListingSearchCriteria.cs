using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Tiêu chí tìm kiếm và phân trang EquipmentListing công khai (Public Equipment Listing Discovery).
/// Không chứa thông tin định danh chủ sở hữu hay thông tin nhạy cảm của các unit vật lý.
/// </summary>
public sealed record EquipmentListingSearchCriteria
{
    public AssetSearchFilters Filters { get; init; }
    public DateOnly? AvailabilityFrom { get; init; }
    public DateOnly? AvailabilityTo { get; init; }
    public int? RequestedQuantity { get; init; }
    public DateTimeOffset AvailabilityEvaluationUtc { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }

    public EquipmentListingSearchCriteria(
        AssetSearchFilters? filters = null,
        DateOnly? availabilityFrom = null,
        DateOnly? availabilityTo = null,
        int? requestedQuantity = null,
        DateTimeOffset? availabilityEvaluationUtc = null,
        int skip = 0,
        int take = 20)
    {
        bool hasFrom = availabilityFrom.HasValue;
        bool hasTo = availabilityTo.HasValue;
        if (hasFrom != hasTo)
        {
            throw new ArgumentException("AvailabilityFrom và AvailabilityTo phải được cung cấp đồng thời.", nameof(availabilityFrom));
        }

        if (requestedQuantity.HasValue && !hasFrom)
        {
            throw new ArgumentException("RequestedQuantity chỉ được truyền khi có khoảng thời gian from/to.", nameof(requestedQuantity));
        }

        Filters = filters ?? new AssetSearchFilters();
        AvailabilityFrom = availabilityFrom;
        AvailabilityTo = availabilityTo;
        RequestedQuantity = requestedQuantity;
        AvailabilityEvaluationUtc = availabilityEvaluationUtc ?? DateTimeOffset.UtcNow;
        Skip = skip;
        Take = take;
    }

    public EquipmentListingSearchCriteria(int skip, int take)
        : this(new AssetSearchFilters(), null, null, null, null, skip, take)
    {
    }
}

/// <summary>
/// Đại diện một Listing công khai cùng số lượng tồn và số lượng khả dụng đã tính toán.
/// </summary>
public sealed record PublicEquipmentListingItem(
    EquipmentListing Listing,
    int TotalQuantity,
    int AvailableQuantity);

/// <summary>
/// Kết quả tìm kiếm Listing công khai bao gồm danh sách items và tổng số lượng khớp.
/// </summary>
public sealed record PublicEquipmentListingSearchResult(
    IReadOnlyList<PublicEquipmentListingItem> Items,
    int TotalCount);
