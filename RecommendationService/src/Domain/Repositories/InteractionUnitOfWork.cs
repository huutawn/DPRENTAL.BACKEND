using System.Data;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class InteractionUnitOfWork : IInteractionUnitOfWork
{
    private readonly RecommendationServiceDbContext _dbContext;

    public InteractionUnitOfWork(RecommendationServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction != null)
        {
            return await action(cancellationToken);
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var result = await action(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        });
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync<object?>(async ct =>
        {
            await action(ct);
            return null;
        }, cancellationToken);
    }

    public async Task RebuildProjectionsAsync(CancellationToken cancellationToken)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            const string rebuildSql = @"
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE recommendation.interaction_events IN SHARE MODE;

                TRUNCATE recommendation.user_equipment_type_interest;

                INSERT INTO recommendation.user_equipment_type_interest (
                    user_id,
                    equipment_type_id,
                    score,
                    last_occurred_at,
                    updated_at
                )
                SELECT
                    user_id,
                    equipment_type_id,
                    SUM(score_weight)::BIGINT AS score,
                    MAX(occurred_at) AS last_occurred_at,
                    clock_timestamp() AS updated_at
                FROM recommendation.interaction_events
                WHERE user_id IS NOT NULL
                  AND equipment_type_id IS NOT NULL
                GROUP BY user_id, equipment_type_id;

                TRUNCATE recommendation.listing_popularity;

                INSERT INTO recommendation.listing_popularity (
                    equipment_listing_id,
                    score,
                    last_occurred_at,
                    updated_at
                )
                SELECT
                    equipment_listing_id,
                    SUM(score_weight)::BIGINT AS score,
                    MAX(occurred_at) AS last_occurred_at,
                    clock_timestamp() AS updated_at
                FROM recommendation.interaction_events
                WHERE equipment_listing_id IS NOT NULL
                GROUP BY equipment_listing_id;
            ";

            await _dbContext.Database.ExecuteSqlRawAsync(rebuildSql, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }
}
