using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class UserEquipmentTypeInterestRepository : IUserEquipmentTypeInterestRepository
{
    private readonly RecommendationServiceDbContext _dbContext;

    public UserEquipmentTypeInterestRepository(RecommendationServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(Guid userId, Guid equipmentTypeId, short scoreWeight, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO recommendation.user_equipment_type_interest (
                user_id,
                equipment_type_id,
                score,
                last_occurred_at,
                updated_at
            ) VALUES (
                {0},
                {1},
                {2},
                {3},
                clock_timestamp()
            )
            ON CONFLICT (user_id, equipment_type_id) DO UPDATE SET
                score = recommendation.user_equipment_type_interest.score + EXCLUDED.score,
                last_occurred_at = GREATEST(recommendation.user_equipment_type_interest.last_occurred_at, EXCLUDED.last_occurred_at),
                updated_at = clock_timestamp();
        ";

        await _dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [userId, equipmentTypeId, (long)scoreWeight, occurredAt],
            cancellationToken);
    }

    public async Task<IReadOnlyList<UserEquipmentTypeInterestItem>> GetTopInterestsAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        return await _dbContext.UserEquipmentTypeInterests
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.EquipmentTypeId)
            .Take(limit)
            .Select(x => new UserEquipmentTypeInterestItem(x.EquipmentTypeId, x.Score))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDistinctUserIdsAsync(Guid? afterUserId, int take, CancellationToken cancellationToken)
    {
        var query = _dbContext.UserEquipmentTypeInterests
            .AsNoTracking();

        if (afterUserId.HasValue)
        {
            var cursor = afterUserId.Value;
            query = query.Where(x => x.UserId.CompareTo(cursor) > 0);
        }

        return await query
            .Select(x => x.UserId)
            .Distinct()
            .OrderBy(u => u)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
