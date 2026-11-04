using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy vấn liên kết media đính kèm đánh giá.
/// </summary>
public interface IReviewMediaRepository
{
    Task<int> CountByReviewIdAsync(Guid reviewId, CancellationToken cancellationToken = default);
    Task<ReviewMedia?> GetByReviewAndMediaIdAsync(Guid reviewId, Guid mediaId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReviewMedia>> GetByReviewIdAsync(Guid reviewId, CancellationToken cancellationToken = default);
    Task AddAsync(ReviewMedia media, CancellationToken cancellationToken = default);
    void Remove(ReviewMedia media);
}
