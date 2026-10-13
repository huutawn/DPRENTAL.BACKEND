using System.Data;
using System.Threading;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai ISearchProjectionStore cho PostgreSQL và In-Memory (unit tests).
/// </summary>
public sealed class SearchProjectionStore : ISearchProjectionStore
{
    private static long _inMemoryRevisionCounter = 100;
    private readonly InventoryServiceDbContext _context;

    public SearchProjectionStore(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<long> GetNextRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            return await _context.Database
                .SqlQueryRaw<long>("SELECT nextval('inventory.search_projection_revision_seq') AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        return Interlocked.Increment(ref _inMemoryRevisionCounter);
    }

    /// <inheritdoc />
    public async Task UpsertJobAsync(
        string entityType,
        Guid entityId,
        string action,
        long desiredRevision,
        bool needsAssetFanout,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (_context.Database.IsNpgsql())
        {
            const string sql = """
                INSERT INTO inventory.search_projection_jobs (
                    id, entity_type, entity_id, action, desired_revision, processing_revision, applied_revision,
                    needs_asset_fanout, fanout_cursor, fanout_completed, status, retry_count,
                    pending_task_uids, created_at, updated_at
                ) VALUES (
                    @pId, @pType, @pEntityId, @pAction, @pDesiredRev, NULL, 0,
                    @pNeedsFanout, NULL, NOT @pNeedsFanout, 'Pending', 0,
                    '{}', @pNow, @pNow
                )
                ON CONFLICT (entity_type, entity_id) DO UPDATE
                SET action = EXCLUDED.action,
                    desired_revision = GREATEST(inventory.search_projection_jobs.desired_revision, EXCLUDED.desired_revision),
                    status = CASE
                        WHEN EXCLUDED.desired_revision > inventory.search_projection_jobs.applied_revision
                             AND inventory.search_projection_jobs.status != 'Processing'
                        THEN 'Pending'
                        ELSE inventory.search_projection_jobs.status
                    END,
                    needs_asset_fanout = inventory.search_projection_jobs.needs_asset_fanout OR EXCLUDED.needs_asset_fanout,
                    fanout_completed = CASE
                        WHEN EXCLUDED.needs_asset_fanout THEN FALSE
                        ELSE inventory.search_projection_jobs.fanout_completed
                    END,
                    updated_at = EXCLUDED.updated_at
                WHERE EXCLUDED.desired_revision >= inventory.search_projection_jobs.desired_revision;
                """;

            var parameters = new[]
            {
                new NpgsqlParameter("pId", Guid.NewGuid()),
                new NpgsqlParameter("pType", entityType),
                new NpgsqlParameter("pEntityId", entityId),
                new NpgsqlParameter("pAction", action),
                new NpgsqlParameter("pDesiredRev", desiredRevision),
                new NpgsqlParameter("pNeedsFanout", needsAssetFanout),
                new NpgsqlParameter("pNow", now)
            };

            await _context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
            return;
        }

        // In-memory fallback
        var existing = await _context.SearchProjectionJobs
            .FirstOrDefaultAsync(j => j.EntityType == entityType && j.EntityId == entityId, cancellationToken);

        if (existing is null)
        {
            var job = SearchProjectionJob.Create(Guid.NewGuid(), entityType, entityId, action, desiredRevision, needsAssetFanout, now);
            _context.SearchProjectionJobs.Add(job);
        }
        else if (desiredRevision >= existing.DesiredRevision)
        {
            _context.Entry(existing).Property("Action").CurrentValue = action;
            _context.Entry(existing).Property("DesiredRevision").CurrentValue = Math.Max(existing.DesiredRevision, desiredRevision);
            if (desiredRevision > existing.AppliedRevision && existing.Status != SearchProjectionJob.StatusProcessing)
            {
                _context.Entry(existing).Property("Status").CurrentValue = SearchProjectionJob.StatusPending;
            }
            if (needsAssetFanout)
            {
                _context.Entry(existing).Property("NeedsAssetFanout").CurrentValue = true;
                _context.Entry(existing).Property("FanoutCompleted").CurrentValue = false;
            }
            _context.Entry(existing).Property("UpdatedAt").CurrentValue = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchProjectionJob>> ClaimPendingBatchAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) return Array.Empty<SearchProjectionJob>();

        var leaseExpiresAt = now.Add(leaseDuration);

        if (_context.Database.IsNpgsql())
        {
            return await _context.SearchProjectionJobs
                .FromSqlInterpolated($"""
                    WITH eligible AS (
                        SELECT j.id
                        FROM inventory.search_projection_jobs j
                        WHERE (
                            (j.status = 'Pending' AND (j.next_attempt_at IS NULL OR j.next_attempt_at <= {now}))
                            OR
                            (j.status = 'Processing' AND j.lock_lease_expires_at <= {now})
                        )
                        AND j.retry_count < 10
                        ORDER BY j.created_at ASC
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE inventory.search_projection_jobs j
                    SET lock_lease_token = {leaseToken},
                        lock_lease_expires_at = {leaseExpiresAt},
                        status = 'Processing',
                        processing_revision = j.desired_revision,
                        updated_at = {now}
                    FROM eligible
                    WHERE j.id = eligible.id
                    RETURNING j.*;
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        // In-memory
        var candidates = await _context.SearchProjectionJobs
            .Where(j => ((j.Status == SearchProjectionJob.StatusPending && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
                        || (j.Status == SearchProjectionJob.StatusProcessing && j.LockLeaseExpiresAt <= now))
                        && j.RetryCount < 10)
            .OrderBy(j => j.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return Array.Empty<SearchProjectionJob>();

        foreach (var job in candidates)
        {
            _context.Entry(job).Property("LockLeaseToken").CurrentValue = leaseToken;
            _context.Entry(job).Property("LockLeaseExpiresAt").CurrentValue = leaseExpiresAt;
            _context.Entry(job).Property("Status").CurrentValue = SearchProjectionJob.StatusProcessing;
            _context.Entry(job).Property("ProcessingRevision").CurrentValue = (long?)job.DesiredRevision;
            _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return candidates;
    }

    /// <inheritdoc />
    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var leaseExpiresAt = now.Add(leaseDuration);

        if (_context.Database.IsNpgsql())
        {
            int affected = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.search_projection_jobs
                SET lock_lease_expires_at = {leaseExpiresAt},
                    updated_at = {now}
                WHERE id = {jobId} AND lock_lease_token = {leaseToken};
                """, cancellationToken);
            return affected > 0;
        }

        var job = await _context.SearchProjectionJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.LockLeaseToken == leaseToken, cancellationToken);
        if (job is null) return false;

        _context.Entry(job).Property("LockLeaseExpiresAt").CurrentValue = leaseExpiresAt;
        _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task SavePendingTasksAsync(
        Guid jobId,
        Guid leaseToken,
        IReadOnlyList<int> taskUids,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var array = taskUids?.ToArray() ?? Array.Empty<int>();

        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.search_projection_jobs
                SET pending_task_uids = {array},
                    updated_at = {now}
                WHERE id = {jobId} AND lock_lease_token = {leaseToken};
                """, cancellationToken);
            return;
        }

        var job = await _context.SearchProjectionJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.LockLeaseToken == leaseToken, cancellationToken);
        if (job != null)
        {
            _context.Entry(job).Property("PendingTaskUids").CurrentValue = array;
            _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task SaveFanoutProgressAsync(
        Guid jobId,
        Guid leaseToken,
        Guid? nextCursor,
        bool completed,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.search_projection_jobs
                SET fanout_cursor = {nextCursor},
                    fanout_completed = {completed},
                    updated_at = {now}
                WHERE id = {jobId} AND lock_lease_token = {leaseToken};
                """, cancellationToken);
            return;
        }

        var job = await _context.SearchProjectionJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.LockLeaseToken == leaseToken, cancellationToken);
        if (job != null)
        {
            _context.Entry(job).Property("FanoutCursor").CurrentValue = nextCursor;
            _context.Entry(job).Property("FanoutCompleted").CurrentValue = completed;
            _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkCompletedAsync(
        Guid jobId,
        Guid leaseToken,
        long processedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            int affected = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.search_projection_jobs
                SET applied_revision = GREATEST(applied_revision, {processedRevision}),
                    processing_revision = NULL,
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    pending_task_uids = ARRAY[]::integer[],
                    status = CASE WHEN desired_revision > {processedRevision} THEN 'Pending' ELSE 'Completed' END,
                    retry_count = 0,
                    error_message = NULL,
                    updated_at = {now}
                WHERE id = {jobId} AND lock_lease_token = {leaseToken};
                """, cancellationToken);
            return affected > 0;
        }

        var job = await _context.SearchProjectionJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.LockLeaseToken == leaseToken, cancellationToken);
        if (job is null) return false;

        _context.Entry(job).Property("AppliedRevision").CurrentValue = Math.Max(job.AppliedRevision, processedRevision);
        _context.Entry(job).Property("ProcessingRevision").CurrentValue = null;
        _context.Entry(job).Property("LockLeaseToken").CurrentValue = null;
        _context.Entry(job).Property("LockLeaseExpiresAt").CurrentValue = null;
        _context.Entry(job).Property("PendingTaskUids").CurrentValue = Array.Empty<int>();
        _context.Entry(job).Property("Status").CurrentValue = job.DesiredRevision > processedRevision ? SearchProjectionJob.StatusPending : SearchProjectionJob.StatusCompleted;
        _context.Entry(job).Property("RetryCount").CurrentValue = 0;
        _context.Entry(job).Property("ErrorMessage").CurrentValue = null;
        _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        Guid jobId,
        Guid leaseToken,
        long processedRevision,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            int affected = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.search_projection_jobs
                SET status = CASE WHEN retry_count + 1 >= 10 THEN 'Failed' ELSE 'Pending' END,
                    retry_count = retry_count + 1,
                    error_message = {errorMessage},
                    next_attempt_at = {nextAttemptAt},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    processing_revision = NULL,
                    updated_at = {now}
                WHERE id = {jobId} AND lock_lease_token = {leaseToken};
                """, cancellationToken);
            return affected > 0;
        }

        var job = await _context.SearchProjectionJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.LockLeaseToken == leaseToken, cancellationToken);
        if (job is null) return false;

        int newRetry = job.RetryCount + 1;
        _context.Entry(job).Property("Status").CurrentValue = newRetry >= 10 ? SearchProjectionJob.StatusFailed : SearchProjectionJob.StatusPending;
        _context.Entry(job).Property("RetryCount").CurrentValue = newRetry;
        _context.Entry(job).Property("ErrorMessage").CurrentValue = errorMessage;
        _context.Entry(job).Property("NextAttemptAt").CurrentValue = nextAttemptAt;
        _context.Entry(job).Property("LockLeaseToken").CurrentValue = null;
        _context.Entry(job).Property("LockLeaseExpiresAt").CurrentValue = null;
        _context.Entry(job).Property("ProcessingRevision").CurrentValue = null;
        _context.Entry(job).Property("UpdatedAt").CurrentValue = now;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<SearchGeneration?> GetGenerationAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SearchGenerations.FirstOrDefaultAsync(g => g.Id == SearchGeneration.DefaultSingletonId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveGenerationAsync(SearchGeneration generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);

        var existing = await _context.SearchGenerations.FirstOrDefaultAsync(g => g.Id == generation.Id, cancellationToken);
        if (existing is null)
        {
            _context.SearchGenerations.Add(generation);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(generation);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SearchBackfillRun?> GetActiveBackfillRunAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SearchBackfillRuns
            .Where(r => r.Status == SearchBackfillRun.StatusBuilding ||
                        r.Status == SearchBackfillRun.StatusCatchingUp ||
                        r.Status == SearchBackfillRun.StatusSwapping)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveBackfillRunAsync(SearchBackfillRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var existing = await _context.SearchBackfillRuns.FirstOrDefaultAsync(r => r.RunId == run.RunId, cancellationToken);
        if (existing is null)
        {
            _context.SearchBackfillRuns.Add(run);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(run);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}
