using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

public interface IReviewProjectionRepository
{
    Task<(IReadOnlyList<ReviewProjection> Items, int TotalCount)> GetPendingEligibleItemsAsync(
        Guid actorId,
        DateTimeOffset nowUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ReviewProjection?> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default);


    /// <summary>
    /// Đọc danh sách bản ghi projection theo danh sách itemId (chỉ đọc AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<ReviewProjection>> GetByItemIdsAsync(
        IEnumerable<Guid> itemIds,
        CancellationToken cancellationToken = default);
    Task UpsertAsync(ReviewProjection projection, CancellationToken cancellationToken = default);
}
