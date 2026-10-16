using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ, truy xuất và quản lý vòng đời của Cache Invalidation Jobs.
/// Hỗ trợ claim theo lô bằng FOR UPDATE SKIP LOCKED và kiểm soát lease token.
/// </summary>
public interface ICacheInvalidationStore
{
    /// <summary>
    /// Claim một lô các công việc invalidation pending hoặc lease đã hết hạn bằng FOR UPDATE SKIP LOCKED.
    /// </summary>
    Task<IReadOnlyList<CacheInvalidationJob>> ClaimEligibleJobsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checkpoint cursor của công việc fanout sau khi hoàn tất một trang listing IDs và trả về trạng thái Pending.
    /// </summary>
    Task<bool> CheckpointFanoutAsync(
        Guid id,
        Guid leaseToken,
        Guid lastListingId,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu công việc đã hoàn thành thành công và giải phóng lease.
    /// </summary>
    Task<bool> MarkAsCompletedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Đánh dấu công việc thất bại, lên lịch retry với exponential backoff hoặc chuyển sang Failed vĩnh viễn.
    /// </summary>
    Task<bool> MarkAsFailedAsync(
        Guid id,
        Guid leaseToken,
        int retryCount,
        DateTimeOffset nextAttemptAtUtc,
        string? errorMessage,
        bool isTerminal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dọn dẹp định kỳ các job Completed đã quá thời hạn lưu trữ (7 ngày).
    /// </summary>
    Task<int> CleanupCompletedJobsAsync(
        DateTimeOffset olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken = default);
}
