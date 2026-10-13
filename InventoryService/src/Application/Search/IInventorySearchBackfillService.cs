namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Kết quả thống kê tiến trình backfill dữ liệu vào Meilisearch.
/// </summary>
public sealed record BackfillResult(
    int PublicListingsCount,
    int OwnerListingsCount,
    int OwnerAssetsCount,
    TimeSpan Elapsed);

/// <summary>
/// Dịch vụ nạp dữ liệu lịch sử (backfill) từ PostgreSQL sang Meilisearch với quy trình staging và swap index an toàn.
/// </summary>
public interface IInventorySearchBackfillService
{
    /// <summary>
    /// Thực hiện nạp toàn bộ danh mục listing và tài sản vào staging indexes, sau đó swap sang live indexes.
    /// </summary>
    Task<BackfillResult> BackfillAsync(CancellationToken cancellationToken = default);
}
