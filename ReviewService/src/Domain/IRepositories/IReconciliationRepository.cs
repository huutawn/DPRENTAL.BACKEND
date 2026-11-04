using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ tiến trình checkpoint quét đối soát (Reconciliation Checkpoints).
/// </summary>
public interface IReconciliationRepository
{
    /// <summary>
    /// Lấy checkpoint gần đây nhất để xác định trạng thái hoàn tất hoặc phục hồi tiến trình dang dở.
    /// </summary>
    Task<ReconciliationCheckpoint?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm mới checkpoint bắt đầu vòng sweep.
    /// </summary>
    Task AddAsync(ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật checkpoint sau khi xử lý xong một trang kết quả hoặc hoàn tất vòng sweep.
    /// </summary>
    Task UpdateAsync(ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken = default);
}
