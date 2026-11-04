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

    public async Task<(IReadOnlyList<Review> Items, int TotalCount)> GetPublicListingReviewsAsync(
        Guid listingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(0, pageSize);
        var skip = (safePage - 1) * safePageSize;

        var query = _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.Direction == ReviewDirection.renter_to_supplier_listing
                     && r.AuthorState == AuthorState.Active
                     && r.ModerationState == ModerationState.Visible
                     && r.EquipmentListingId == listingId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<Review> Items, int TotalCount)> GetPublicSupplierReviewsAsync(
        Guid supplierId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(0, pageSize);
        var skip = (safePage - 1) * safePageSize;

        var query = _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.Direction == ReviewDirection.renter_to_supplier_listing
                     && r.AuthorState == AuthorState.Active
                     && r.ModerationState == ModerationState.Visible
                     && r.SubjectId == supplierId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<Review> Items, int TotalCount)> GetMyReviewsAsync(
        Guid authorId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(0, pageSize);
        var skip = (safePage - 1) * safePageSize;

        var query = _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.AuthorId == authorId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<(int TotalCount, double? AverageRating, Dictionary<int, int> Distribution)> GetRatingSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var isListing = string.Equals(targetType, "listing", StringComparison.OrdinalIgnoreCase);
        var isSupplier = string.Equals(targetType, "supplier", StringComparison.OrdinalIgnoreCase);

        if (!isListing && !isSupplier)
        {
            throw new ArgumentException($"Invalid target type: '{targetType}'. Expected 'listing' or 'supplier'.", nameof(targetType));
        }

        var baseQuery = _dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.Direction == ReviewDirection.renter_to_supplier_listing
                     && r.AuthorState == AuthorState.Active
                     && r.ModerationState == ModerationState.Visible);

        var query = isListing
            ? baseQuery.Where(r => r.EquipmentListingId == targetId)
            : baseQuery.Where(r => r.SubjectId == targetId);

        var distribution = new Dictionary<int, int>
        {
            [1] = 0,
            [2] = 0,
            [3] = 0,
            [4] = 0,
            [5] = 0
        };

        var ratingGroups = await query
            .GroupBy(r => r.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var totalCount = 0;
        long totalSum = 0;

        foreach (var group in ratingGroups)
        {
            if (distribution.ContainsKey(group.Rating))
            {
                distribution[group.Rating] = group.Count;
            }

            totalCount += group.Count;
            totalSum += (long)group.Rating * group.Count;
        }

        double? averageRating = totalCount == 0
            ? null
            : Math.Round((double)totalSum / totalCount, 1, MidpointRounding.AwayFromZero);

        return (totalCount, averageRating, distribution);
    }
}
