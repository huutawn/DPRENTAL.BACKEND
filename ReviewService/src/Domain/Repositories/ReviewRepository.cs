using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai EF Core cho IReviewRepository.
/// </summary>
public sealed class ReviewRepository : IReviewRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public ReviewRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Reviews
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task AddAsync(Review review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);

        await _dbContext.Reviews.AddAsync(review, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Review review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);

        var entry = _dbContext.Entry(review);
        if (entry.State == EntityState.Detached)
        {
            _dbContext.Reviews.Attach(review);
            entry.State = EntityState.Modified;
        }
        else if (entry.State == EntityState.Unchanged)
        {
            entry.State = EntityState.Modified;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsByItemAndDirectionAsync(
        Guid itemId,
        ReviewDirection direction,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Reviews
            .AnyAsync(r => r.ItemId == itemId && r.Direction == direction, cancellationToken);
    }

    public async Task<bool> ExistsByItemAndDirectionAsync(
        Guid itemId,
        string direction,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return false;
        }

        if (Enum.TryParse<ReviewDirection>(direction, true, out var parsedDirection))
        {
            return await ExistsByItemAndDirectionAsync(itemId, parsedDirection, cancellationToken);
        }

        return false;
    }

    public async Task<Review?> GetByItemAndDirectionAsync(
        Guid itemId,
        ReviewDirection direction,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Reviews
            .FirstOrDefaultAsync(r => r.ItemId == itemId && r.Direction == direction, cancellationToken);
    }
}
