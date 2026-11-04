using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy vấn thực thể Review.
/// </summary>
public interface IReviewRepository
{
    /// <summary>
    /// Tìm đánh giá theo khóa chính (UUID).
    /// </summary>
    Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm mới một bản ghi đánh giá vào cơ sở dữ liệu.
    /// </summary>
    Task AddAsync(Review review, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật thay đổi của bản ghi đánh giá (kèm kiểm tra Concurrency Token).
    /// </summary>
    Task UpdateAsync(Review review, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra sự tồn tại của đánh giá cho một mục thuê theo chiều chỉ định.
    /// </summary>
    Task<bool> ExistsByItemAndDirectionAsync(Guid itemId, ReviewDirection direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra sự tồn tại của đánh giá cho một mục thuê theo chuỗi biểu diễn chiều.
    /// </summary>
    Task<bool> ExistsByItemAndDirectionAsync(Guid itemId, string direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm đánh giá theo mục thuê và chiều đánh giá.
    /// </summary>
    Task<Review?> GetByItemAndDirectionAsync(Guid itemId, ReviewDirection direction, CancellationToken cancellationToken = default);
}
