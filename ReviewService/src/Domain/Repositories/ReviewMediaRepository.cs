using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai EF Core cho IReviewMediaRepository.
/// </summary>
public sealed class ReviewMediaRepository : IReviewMediaRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public ReviewMediaRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<int> CountByReviewIdAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ReviewMedia
            .CountAsync(m => m.ReviewId == reviewId, cancellationToken);
    }

    public Task<ReviewMedia?> GetByReviewAndMediaIdAsync(Guid reviewId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ReviewMedia
            .FirstOrDefaultAsync(m => m.ReviewId == reviewId && m.MediaId == mediaId, cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewMedia>> GetByReviewIdAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ReviewMedia
            .AsNoTracking()
            .Where(m => m.ReviewId == reviewId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(ReviewMedia media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        await _dbContext.ReviewMedia.AddAsync(media, cancellationToken);
    }

    public void Remove(ReviewMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _dbContext.ReviewMedia.Remove(media);
    }
}
