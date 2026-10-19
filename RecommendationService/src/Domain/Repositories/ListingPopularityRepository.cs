using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class ListingPopularityRepository : IListingPopularityRepository
{
    private readonly RecommendationServiceDbContext _dbContext;

    public ListingPopularityRepository(RecommendationServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(Guid equipmentListingId, short scoreWeight, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO recommendation.listing_popularity (
                equipment_listing_id,
                score,
                last_occurred_at,
                updated_at
            ) VALUES (
                {0},
                {1},
                {2},
                clock_timestamp()
            )
            ON CONFLICT (equipment_listing_id) DO UPDATE SET
                score = recommendation.listing_popularity.score + EXCLUDED.score,
                last_occurred_at = GREATEST(recommendation.listing_popularity.last_occurred_at, EXCLUDED.last_occurred_at),
                updated_at = clock_timestamp();
        ";

        await _dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [equipmentListingId, (long)scoreWeight, occurredAt],
            cancellationToken);
    }

    public async Task<IReadOnlyList<ListingPopularityItem>> GetTopPopularListingsAsync(int limit, CancellationToken cancellationToken)
    {
        return await _dbContext.ListingPopularities
            .AsNoTracking()
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.EquipmentListingId)
            .Take(limit)
            .Select(x => new ListingPopularityItem(x.EquipmentListingId, x.Score))
            .ToListAsync(cancellationToken);
    }
}
