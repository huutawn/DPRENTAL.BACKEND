using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store quản lý Cache Invalidation Jobs với FOR UPDATE SKIP LOCKED và lease tokens trên PostgreSQL.
/// </summary>
public sealed class CacheInvalidationStore : ICacheInvalidationStore
{
    private readonly InventoryServiceDbContext _context;

    public CacheInvalidationStore(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CacheInvalidationJob>> ClaimEligibleJobsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "BatchSize phải lớn hơn 0.");
        }

        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt = nowUtc.Add(leaseDuration);

        if (_context.Database.IsNpgsql())
        {
            return await _context.CacheInvalidationJobs
                .FromSqlInterpolated($"""
                    WITH eligible AS (
                        SELECT id
                        FROM inventory.cache_invalidation_jobs
                        WHERE (
                            status = {CacheInvalidationJob.StatusPending} AND next_attempt_at <= {nowUtc}
                        ) OR (
                            status = {CacheInvalidationJob.StatusProcessing} AND lock_lease_expires_at <= {nowUtc}
                        )
                        ORDER BY created_at, id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    ),
                    claimed AS (
                        UPDATE inventory.cache_invalidation_jobs j
                        SET status = {CacheInvalidationJob.StatusProcessing},
                            lock_lease_token = {leaseToken},
                            lock_lease_expires_at = {leaseExpiresAt}
                        FROM eligible
                        WHERE j.id = eligible.id
                        RETURNING j.*
                    )
                    SELECT * FROM claimed;
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        // Fallback InMemory cho test
        var candidates = await _context.CacheInvalidationJobs
            .Where(j => ((j.Status == CacheInvalidationJob.StatusPending && j.NextAttemptAt <= nowUtc) ||
                         (j.Status == CacheInvalidationJob.StatusProcessing && j.LockLeaseExpiresAt <= nowUtc)) &&
                        j.Status != CacheInvalidationJob.StatusCompleted &&
                        j.Status != CacheInvalidationJob.StatusFailed)
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var job in candidates)
        {
            job.AssignLease(leaseToken, leaseExpiresAt);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return candidates;
    }

    /// <inheritdoc />
    public async Task<bool> CheckpointFanoutAsync(
        Guid id,
        Guid leaseToken,
        Guid lastListingId,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var emptyScopes = Array.Empty<string>();
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.cache_invalidation_jobs
                SET status = {CacheInvalidationJob.StatusPending},
                    fanout_after_listing_id = {lastListingId},
                    scopes = {emptyScopes},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    next_attempt_at = {nextAttemptAtUtc},
                    last_error = NULL
                WHERE id = {id} AND lock_lease_token = {leaseToken}
                """, cancellationToken);

            return rows > 0;
        }

        var job = await _context.CacheInvalidationJobs
            .FirstOrDefaultAsync(j => j.Id == id && j.LockLeaseToken == leaseToken, cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.CheckpointFanout(lastListingId, nextAttemptAtUtc);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsCompletedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.cache_invalidation_jobs
                SET status = {CacheInvalidationJob.StatusCompleted},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    processed_at = {completedAtUtc},
                    last_error = NULL
                WHERE id = {id} AND lock_lease_token = {leaseToken}
                """, cancellationToken);

            return rows > 0;
        }

        var job = await _context.CacheInvalidationJobs
            .FirstOrDefaultAsync(j => j.Id == id && j.LockLeaseToken == leaseToken, cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.MarkCompleted(completedAtUtc);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsFailedAsync(
        Guid id,
        Guid leaseToken,
        int retryCount,
        DateTimeOffset nextAttemptAtUtc,
        string? errorMessage,
        bool isTerminal,
        CancellationToken cancellationToken = default)
    {
        var targetStatus = isTerminal ? CacheInvalidationJob.StatusFailed : CacheInvalidationJob.StatusPending;

        if (_context.Database.IsNpgsql())
        {
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.cache_invalidation_jobs
                SET status = {targetStatus},
                    retry_count = {retryCount},
                    next_attempt_at = {nextAttemptAtUtc},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    last_error = {errorMessage}
                WHERE id = {id} AND lock_lease_token = {leaseToken}
                """, cancellationToken);

            return rows > 0;
        }

        var job = await _context.CacheInvalidationJobs
            .FirstOrDefaultAsync(j => j.Id == id && j.LockLeaseToken == leaseToken, cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.MarkFailed(retryCount, nextAttemptAtUtc, errorMessage, isTerminal);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> CleanupCompletedJobsAsync(
        DateTimeOffset olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            return await _context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM inventory.cache_invalidation_jobs
                WHERE id IN (
                    SELECT id FROM inventory.cache_invalidation_jobs
                    WHERE status = {CacheInvalidationJob.StatusCompleted} AND processed_at < {olderThanUtc}
                    LIMIT {batchSize}
                )
                """, cancellationToken);
        }

        var completed = await _context.CacheInvalidationJobs
            .Where(j => j.Status == CacheInvalidationJob.StatusCompleted && j.ProcessedAt < olderThanUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        _context.CacheInvalidationJobs.RemoveRange(completed);
        await _context.SaveChangesAsync(cancellationToken);
        return completed.Count;
    }
}
