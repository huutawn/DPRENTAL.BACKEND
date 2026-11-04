using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

public interface IReviewReportRepository
{
    Task<ReviewReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ReviewReport?> GetByReviewAndReporterAsync(Guid reviewId, Guid reporterId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ReviewReport> Items, int TotalCount)> GetReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task AddAsync(ReviewReport report, CancellationToken cancellationToken = default);
    Task UpdateAsync(ReviewReport report, CancellationToken cancellationToken = default);
}
