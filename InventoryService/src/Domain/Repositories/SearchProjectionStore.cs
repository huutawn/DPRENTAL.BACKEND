using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>PostgreSQL owns leases, immutable submissions and revision-scoped fan-out progress.</summary>
public sealed partial class SearchProjectionStore(InventoryServiceDbContext context) : ISearchProjectionStore
{
    public async Task UpsertJobAsync(string entityType, Guid entityId, string action, long desiredRevision,
        bool needsAssetFanout, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desiredRevision);
        const string sql = """
            INSERT INTO inventory.search_projection_jobs
                (id, entity_type, entity_id, action, desired_revision, applied_revision,
                 needs_asset_fanout, fanout_completed, fanout_requested_revision, fanout_applied_revision,
                 submission_started, submission_uncertain, submission_complete,
                 status, retry_count, pending_task_uids, created_at, updated_at)
            VALUES (@id, @type, @entity, @action, @revision, 0, @fanout, NOT @fanout,
                    CASE WHEN @fanout THEN @revision ELSE 0 END, 0, FALSE, FALSE, FALSE,
                    'Pending', 0, ARRAY[]::integer[], @now, @now)
            ON CONFLICT (entity_type, entity_id) DO UPDATE SET
                action = EXCLUDED.action,
                desired_revision = EXCLUDED.desired_revision,
                status = CASE WHEN inventory.search_projection_jobs.status = 'Processing' THEN 'Processing'
                              WHEN EXCLUDED.desired_revision > inventory.search_projection_jobs.applied_revision
                              THEN 'Pending' ELSE inventory.search_projection_jobs.status END,
                retry_count = CASE WHEN EXCLUDED.desired_revision > inventory.search_projection_jobs.desired_revision
                                   THEN 0 ELSE inventory.search_projection_jobs.retry_count END,
                next_attempt_at = CASE WHEN EXCLUDED.desired_revision > inventory.search_projection_jobs.desired_revision
                                       THEN NULL ELSE inventory.search_projection_jobs.next_attempt_at END,
                needs_asset_fanout = inventory.search_projection_jobs.needs_asset_fanout OR EXCLUDED.needs_asset_fanout,
                fanout_requested_revision = GREATEST(inventory.search_projection_jobs.fanout_requested_revision,
                                                    EXCLUDED.fanout_requested_revision),
                updated_at = EXCLUDED.updated_at
            WHERE EXCLUDED.desired_revision > inventory.search_projection_jobs.desired_revision;
            """;
        await context.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter[]
        {
            new("id", Guid.NewGuid()), new("type", entityType), new("entity", entityId), new("action", action),
            new("revision", desiredRevision), new("fanout", needsAssetFanout), new("now", now)
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchProjectionJob>> ClaimPendingBatchAsync(int batchSize, Guid leaseToken,
        TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) return [];
        var expiry = now.Add(leaseDuration);
        return await context.SearchProjectionJobs.FromSqlInterpolated($"""
            WITH eligible AS (
                SELECT id FROM inventory.search_projection_jobs
                WHERE ((status = 'Pending' AND (next_attempt_at IS NULL OR next_attempt_at <= {now}))
                    OR (status = 'Processing' AND lock_lease_expires_at <= {now})) AND retry_count < 10
                ORDER BY created_at, id LIMIT {batchSize} FOR UPDATE SKIP LOCKED
            )
            UPDATE inventory.search_projection_jobs j SET
                lock_lease_token = {leaseToken}, lock_lease_expires_at = {expiry}, status = 'Processing',
                processing_revision = CASE WHEN j.submission_started AND j.submission_revision IS NOT NULL
                                           THEN j.submission_revision ELSE j.desired_revision END,
                fanout_cursor = CASE WHEN NOT j.submission_started
                    AND j.fanout_requested_revision > j.fanout_applied_revision
                    AND j.fanout_revision IS DISTINCT FROM j.desired_revision THEN NULL ELSE j.fanout_cursor END,
                fanout_completed = CASE WHEN NOT j.submission_started
                    AND j.fanout_requested_revision > j.fanout_applied_revision THEN FALSE ELSE j.fanout_completed END,
                fanout_revision = CASE WHEN NOT j.submission_started
                    AND j.fanout_requested_revision > j.fanout_applied_revision
                    THEN j.desired_revision ELSE j.fanout_revision END, updated_at = {now}
            FROM eligible WHERE j.id = eligible.id RETURNING j.*
            """).AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, Guid leaseToken, TimeSpan leaseDuration,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET lock_lease_expires_at = {now.Add(leaseDuration)}, updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND status = 'Processing'
              AND lock_lease_expires_at > {now}
            """, cancellationToken) == 1;

    public async Task<bool> SaveSubmissionAsync(Guid jobId, Guid leaseToken, long revision,
        IReadOnlyList<int> taskUids, bool uncertain, bool complete, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET pending_task_uids = {taskUids.ToArray()},
                submission_revision = {revision}, submission_started = TRUE, submission_uncertain = {uncertain},
                submission_complete = {complete}, updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND processing_revision = {revision}
              AND lock_lease_expires_at > {now}
            """, cancellationToken) == 1;

    public async Task<bool> ResetSubmissionAsync(Guid jobId, Guid leaseToken, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET pending_task_uids = ARRAY[]::integer[],
                submission_revision = NULL, submission_started = FALSE, submission_uncertain = FALSE,
                submission_complete = FALSE, updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND lock_lease_expires_at > {now}
            """, cancellationToken) == 1;

    public async Task<bool> SaveFanoutProgressAsync(Guid jobId, Guid leaseToken, long revision, Guid? nextCursor,
        bool completed, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET fanout_cursor = {nextCursor}, fanout_completed = {completed},
                fanout_applied_revision = CASE WHEN {completed} THEN {revision} ELSE fanout_applied_revision END,
                updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND fanout_revision = {revision}
              AND processing_revision = {revision} AND lock_lease_expires_at > {now}
            """, cancellationToken) == 1;

    public async Task<bool> MarkCompletedAsync(Guid jobId, Guid leaseToken, long processedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET applied_revision = GREATEST(applied_revision, {processedRevision}),
                processing_revision = NULL, lock_lease_token = NULL, lock_lease_expires_at = NULL,
                pending_task_uids = ARRAY[]::integer[], submission_revision = NULL, submission_started = FALSE,
                submission_complete = FALSE, submission_uncertain = FALSE,
                status = CASE WHEN desired_revision > {processedRevision}
                                   OR fanout_requested_revision > fanout_applied_revision THEN 'Pending' ELSE 'Completed' END,
                retry_count = 0, next_attempt_at = NULL, error_message = NULL, updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND processing_revision = {processedRevision}
              AND submission_revision = {processedRevision} AND submission_complete AND NOT submission_uncertain
              AND lock_lease_expires_at > {now}
            """, cancellationToken) == 1;

    public async Task<bool> MarkFailedAsync(Guid jobId, Guid leaseToken, long processedRevision, string errorMessage,
        DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.search_projection_jobs SET status = CASE WHEN retry_count + 1 >= 10 THEN 'Failed' ELSE 'Pending' END,
                retry_count = retry_count + 1, error_message = {errorMessage}, next_attempt_at = {nextAttemptAt},
                lock_lease_token = NULL, lock_lease_expires_at = NULL, processing_revision = NULL, updated_at = {now}
            WHERE id = {jobId} AND lock_lease_token = {leaseToken} AND processing_revision = {processedRevision}
            """, cancellationToken) == 1;
}
