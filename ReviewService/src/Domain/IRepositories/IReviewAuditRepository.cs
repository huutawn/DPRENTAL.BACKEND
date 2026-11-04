using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ nhật ký kiểm toán ReviewAudit.
/// </summary>
public interface IReviewAuditRepository
{
    /// <summary>
    /// Ghi nhận một bản ghi kiểm toán mới vào cơ sở dữ liệu.
    /// </summary>
    Task AddAsync(ReviewAudit audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách lịch sử kiểm toán của một đánh giá theo thứ tự thời gian giảm dần.
    /// </summary>
    Task<IReadOnlyList<ReviewAudit>> GetByReviewIdAsync(Guid reviewId, CancellationToken cancellationToken = default);
}
