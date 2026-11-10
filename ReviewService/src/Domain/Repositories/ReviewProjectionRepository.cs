using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

public sealed class ReviewProjectionRepository : IReviewProjectionRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public ReviewProjectionRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(IReadOnlyList<ReviewProjection> Items, int TotalCount)> GetPendingEligibleItemsAsync(
        Guid actorId,
        DateTimeOffset nowUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 50) pageSize = 50;

        // Đọc từ review_projections với điều kiện:
        // 1. actor là renter hoặc supplier
        // 2. is_eligible == true
        // 3. deadline_at > nowUtc (chưa hết hạn 30 ngày)
        // 4. NOT EXISTS trong bảng reviews cho cùng item_id và direction tương ứng
        var query = _dbContext.ReviewProjections
            .AsNoTracking()
            .Where(p => (p.RenterId == actorId || p.SupplierId == actorId)
                        && p.IsEligible
                        && p.DeadlineAt.HasValue
                        && p.DeadlineAt.Value > nowUtc)
            .Where(p => !_dbContext.Reviews.Any(r =>
                r.ItemId == p.ItemId &&
                (p.RenterId == actorId
                    ? r.Direction == Common.Enums.ReviewDirection.renter_to_supplier_listing
                    : r.Direction == Common.Enums.ReviewDirection.supplier_to_renter)))
            .OrderByDescending(p => p.CompletedAt)
            .ThenByDescending(p => p.Id);

        int totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public Task<ReviewProjection?> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ReviewProjections
            .FirstOrDefaultAsync(p => p.ItemId == itemId, cancellationToken);
    }

    public async Task<IReadOnlyList<ReviewProjection>> GetByItemIdsAsync(
        IEnumerable<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var idList = itemIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return Array.Empty<ReviewProjection>();
        }

        return await _dbContext.ReviewProjections
            .AsNoTracking()
            .Where(p => idList.Contains(p.ItemId))
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(ReviewProjection projection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (!projection.CompletedAt.HasValue || projection.CompletedAt.Value == default)
        {
            throw new ArgumentException("CompletedAt must have a valid authoritative timestamp and cannot be null or default.", nameof(projection));
        }

        if (!projection.DeadlineAt.HasValue || projection.DeadlineAt.Value == default)
        {
            throw new ArgumentException("DeadlineAt must have a valid timestamp and cannot be null or default.", nameof(projection));
        }

        var existing = await _dbContext.ReviewProjections
            .FirstOrDefaultAsync(p => p.ItemId == projection.ItemId, cancellationToken);

        if (existing == null)
        {
            await _dbContext.ReviewProjections.AddAsync(projection, cancellationToken);
        }
        else
        {
            DateTimeOffset completedAt = existing.CompletedAt ?? projection.CompletedAt.Value;
            DateTimeOffset deadlineAt = existing.DeadlineAt ?? projection.DeadlineAt.Value;

            existing.UpdateEligibility(
                projection.SupplierId,
                completedAt,
                deadlineAt,
                projection.IsEligible,
                DateTimeOffset.UtcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
