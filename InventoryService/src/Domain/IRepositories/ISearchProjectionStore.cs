using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Giao diện quản lý hàng đợi Search Projection Jobs, Sequence revision và trạng thái Backfill Generation.
/// </summary>
public interface ISearchProjectionStore
{
    /// <summary>
    /// Lấy giá trị revision tiếp theo từ sequence search_projection_revision_seq.
    /// </summary>
    Task<long> GetNextRevisionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi nhận hoặc cập nhật desired_revision cho entity; bảo vệ lease đang chạy nếu job đang Processing.
    /// </summary>
    Task UpsertJobAsync(
        string entityType,
        Guid entityId,
        string action,
        long desiredRevision,
        bool needsAssetFanout,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claim lô công việc: Pending đến hạn hoặc Processing nhưng đã hết hạn lease (reclaim).
    /// </summary>
    Task<IReadOnlyList<SearchProjectionJob>> ClaimPendingBatchAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gia hạn lease token cho công việc đang xử lý. Trả về false nếu worker đã mất lease.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu danh sách Meilisearch task UID ngay sau khi submit để phục hồi khi restart/timeout.
    /// </summary>
    Task SavePendingTasksAsync(
        Guid jobId,
        Guid leaseToken,
        IReadOnlyList<int> taskUids,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu tiến độ duyệt keyset fan-out asset con.
    /// </summary>
    Task SaveFanoutProgressAsync(
        Guid jobId,
        Guid leaseToken,
        Guid? nextCursor,
        bool completed,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xác nhận hoàn tất xử lý revision. Nếu có desired_revision mới hơn thì chuyển về Pending.
    /// </summary>
    Task<bool> MarkCompletedAsync(
        Guid jobId,
        Guid leaseToken,
        long processedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu thất bại và lên lịch retry với backoff.
    /// </summary>
    Task<bool> MarkFailedAsync(
        Guid jobId,
        Guid leaseToken,
        long processedRevision,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy generation live hiện tại.
    /// </summary>
    Task<SearchGeneration?> GetGenerationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu hoặc cập nhật generation live.
    /// </summary>
    Task SaveGenerationAsync(SearchGeneration generation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy đợt chạy backfill đang hoạt động (nếu có).
    /// </summary>
    Task<SearchBackfillRun?> GetActiveBackfillRunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu hoặc cập nhật đợt chạy backfill.
    /// </summary>
    Task SaveBackfillRunAsync(SearchBackfillRun run, CancellationToken cancellationToken = default);
}
