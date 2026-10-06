using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Tiêu chí tìm kiếm và phân trang tài sản công khai (Public Asset Search).
/// Không chứa thông tin định danh chủ sở hữu hay cờ ghi đè hiển thị.
/// </summary>
public sealed record PublicAssetSearchCriteria
{
    public AssetSearchFilters Filters { get; init; }
    public DateOnly? AvailabilityFrom { get; init; }
    public DateOnly? AvailabilityTo { get; init; }
    public DateTimeOffset? AvailabilityEvaluationUtc { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }

    public PublicAssetSearchCriteria(
        AssetSearchFilters? filters = null,
        DateOnly? availabilityFrom = null,
        DateOnly? availabilityTo = null,
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

        if (hasFrom && !availabilityEvaluationUtc.HasValue)
        {
            throw new ArgumentException("AvailabilityEvaluationUtc là bắt buộc khi có cửa sổ ngày khả dụng.", nameof(availabilityEvaluationUtc));
        }

        Filters = filters ?? new AssetSearchFilters();
        AvailabilityFrom = availabilityFrom;
        AvailabilityTo = availabilityTo;
        AvailabilityEvaluationUtc = availabilityEvaluationUtc;
        Skip = skip;
        Take = take;
    }

    public PublicAssetSearchCriteria(int skip, int take)
        : this(new AssetSearchFilters(), null, null, null, skip, take)
    {
    }
}

/// <summary>
/// Kết quả tìm kiếm tài sản công khai bao gồm danh sách tài sản và tổng số lượng khớp.
/// </summary>
public sealed record PublicAssetSearchResult(
    IReadOnlyList<InventoryAsset> Items,
    int TotalCount);
