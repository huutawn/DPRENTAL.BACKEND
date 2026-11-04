using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

public sealed class ReviewReportRepository : IReviewReportRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public ReviewReportRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<ReviewReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _dbContext.ReviewReports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public Task<ReviewReport?> GetByReviewAndReporterAsync(Guid reviewId, Guid reporterId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ReviewReports
            .FirstOrDefaultAsync(r => r.ReviewId == reviewId && r.ReporterId == reporterId, cancellationToken);
    }

    public async Task<(IReadOnlyList<ReviewReport> Items, int TotalCount)> GetReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 50) pageSize = 50;

        var query = _dbContext.ReviewReports
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt);

        int totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(ReviewReport report, CancellationToken cancellationToken = default)
    {
        await _dbContext.ReviewReports.AddAsync(report, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ReviewReport report, CancellationToken cancellationToken = default)
    {
        _dbContext.ReviewReports.Update(report);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
