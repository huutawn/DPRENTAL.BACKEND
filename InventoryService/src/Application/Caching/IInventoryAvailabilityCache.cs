using Dprental.InventoryService.Application.DTOs.Availability;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Dữ liệu cache của một tháng bao gồm danh sách các khoảng bận và mốc hết hạn nội bộ (UTC).
/// </summary>
public sealed record CachedMonthRanges(
    IReadOnlyList<BusyRangeDto> Ranges,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// Hợp đồng cache tính khả dụng theo tháng cho tài sản (Valkey Read Cache theo DPR-150).
/// </summary>
public interface IInventoryAvailabilityCache
{
    /// <summary>
    /// Cho biết cache có đang được kích hoạt hay không.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Đọc bulk các tháng bận của tài sản từ cache.
    /// Trả về dictionary: monthKey ("yyyyMM") -> CachedMonthRanges (hoặc null nếu miss/hết hạn/payload hỏng).
    /// </summary>
    Task<IReadOnlyDictionary<string, CachedMonthRanges?>> GetMonthRangesBulkAsync(
        Guid assetId,
        IReadOnlyList<string> monthKeys,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi cache cho một tháng của tài sản kèm mốc hết hiệu lực tuyệt đối và cập nhật atomic metadata index.
    /// Bỏ qua ghi nếu deadline đã qua.
    /// </summary>
    Task SetMonthRangesAsync(
        Guid assetId,
        string monthKey,
        IReadOnlyList<BusyRangeDto> ranges,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidate toàn bộ các tháng của tài sản dựa trên metadata index theo thao tác atomic.
    /// </summary>
    Task InvalidateAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);
}
