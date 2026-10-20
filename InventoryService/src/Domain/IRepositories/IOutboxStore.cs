using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ, truy xuất và quản lý vòng đời của Transactional Outbox messages.
/// Hỗ trợ claim theo lô bằng FOR UPDATE SKIP LOCKED, kiểm soát lease token và strict per-key ordering.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Thêm thông điệp Outbox mới vào cơ sở dữ liệu trong cùng transaction với thay đổi nghiệp vụ.
    /// </summary>
    void Add(OutboxMessage message);


    /// <summary>
    /// Lưu các thay đổi của outbox messages xuống persistence storage.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    /// <summary>
    /// Tìm thông điệp Outbox theo Id.
    /// </summary>
    Task<OutboxMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claim một lô các thông điệp pending/retry đủ điều kiện bằng transaction ngắn với FOR UPDATE SKIP LOCKED.
    /// Đảm bảo không publish vượt bản ghi pending trước đó cùng partition key (strict per-key ordering).
    /// </summary>
    /// <param name="batchSize">Số lượng tối đa thông điệp cần claim.</param>
    /// <param name="leaseToken">Định danh lease của worker đang claim.</param>
    /// <param name="leaseDuration">Thời hạn hiệu lực của lease.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các thông điệp đã được gán lease thành công.</returns>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu thông điệp đã được Broker ACK thành công bằng lease token.
    /// Chỉ ghi nhận thành công nếu lease token khớp (ngăn worker mất lease ghi đè).
    /// </summary>
    Task<bool> MarkAsPublishedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ghi nhận thất bại khi xuất bản thông điệp, cập nhật lịch backoff và nhả lease nếu lease token khớp.
    /// </summary>
    Task<bool> RecordFailureAsync(
        Guid id,
        Guid leaseToken,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset? leaseExpiry = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phục hồi thủ công (requeue) cho thông điệp đã cạn budget retry (retry_count >= 10).
    /// </summary>
    Task<bool> RequeueAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
