using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class DeliveryJobRepository : IDeliveryJobRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public DeliveryJobRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(NotificationDeliveryJob job, CancellationToken cancellationToken = default)
    {
        await _dbContext.DeliveryJobs.AddAsync(job, cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<NotificationDeliveryJob> jobs, CancellationToken cancellationToken = default)
    {
        await _dbContext.DeliveryJobs.AddRangeAsync(jobs, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDeliveryJob>> ClaimPendingJobsAsync(
        string workerId,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return Array.Empty<NotificationDeliveryJob>();
        }

        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.Add(leaseDuration);
        var leaseToken = $"{workerId}_{Guid.NewGuid():N}";

        if (_dbContext.Database.IsNpgsql())
        {
            const string sql = @"
UPDATE notification.delivery_jobs
SET status = 'Processing',
    lease_token = @leaseToken,
    lease_expires_at = @leaseExpiresAt,
    attempt_count = attempt_count + 1,
    updated_at = @now
WHERE id IN (
    SELECT id
    FROM notification.delivery_jobs
    WHERE status IN ('Pending', 'RetryScheduled')
      AND next_attempt_at <= @now
    ORDER BY next_attempt_at ASC
    LIMIT @batchSize
    FOR UPDATE SKIP LOCKED
);";

            var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(
                sql,
                new NpgsqlParameter("leaseToken", leaseToken),
                new NpgsqlParameter("leaseExpiresAt", leaseExpiresAt),
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("batchSize", batchSize),
                cancellationToken);

            if (rowsAffected == 0)
            {
                return Array.Empty<NotificationDeliveryJob>();
            }

            return await _dbContext.DeliveryJobs
                .Include(j => j.Notification)
                .Where(j => j.LeaseToken == leaseToken)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Fallback for InMemory provider / unit tests
            var candidateJobs = await _dbContext.DeliveryJobs
                .Include(j => j.Notification)
                .Where(j => (j.Status == "Pending" || j.Status == "RetryScheduled") && j.NextAttemptAt <= now)
                .OrderBy(j => j.NextAttemptAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var job in candidateJobs)
            {
                job.Status = "Processing";
                job.LeaseToken = leaseToken;
                job.LeaseExpiresAt = leaseExpiresAt;
                job.AttemptCount += 1;
                job.UpdatedAt = now;
            }

            if (candidateJobs.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return candidateJobs;
        }
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_dbContext.Database.IsNpgsql())
        {
            var uncertainCount = await _dbContext.DeliveryJobs
                .Where(j => j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt <= now && j.DispatchStartedAt != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "Uncertain")
                    .SetProperty(j => j.LastErrorCode, "LEASE_EXPIRED_DURING_DISPATCH")
                    .SetProperty(j => j.LastErrorDetail, "Worker lease expired while dispatch was already started")
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);

            var failedCount = await _dbContext.DeliveryJobs
                .Where(j => j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt <= now && j.DispatchStartedAt == null && j.AttemptCount >= j.MaxAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "Failed")
                    .SetProperty(j => j.LastErrorCode, "MAX_ATTEMPTS_EXCEEDED")
                    .SetProperty(j => j.LastErrorDetail, "Exceeded max processing attempts")
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);

            var pendingCount = await _dbContext.DeliveryJobs
                .Where(j => j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt <= now && j.DispatchStartedAt == null && j.AttemptCount < j.MaxAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "Pending")
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);

            return pendingCount + failedCount + uncertainCount;
        }
        else
        {
            var jobs = await _dbContext.DeliveryJobs
                .Where(j => j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt <= now)
                .ToListAsync(cancellationToken);

            foreach (var job in jobs)
            {
                if (job.DispatchStartedAt != null)
                {
                    job.Status = "Uncertain";
                    job.LastErrorCode = "LEASE_EXPIRED_DURING_DISPATCH";
                    job.LastErrorDetail = "Worker lease expired while dispatch was already started";
                }
                else if (job.AttemptCount >= job.MaxAttempts)
                {
                    job.Status = "Failed";
                    job.LastErrorCode = "MAX_ATTEMPTS_EXCEEDED";
                    job.LastErrorDetail = $"Exceeded max processing attempts ({job.MaxAttempts})";
                }
                else
                {
                    job.Status = "Pending";
                }
                job.LeaseToken = null;
                job.LeaseExpiresAt = null;
                job.UpdatedAt = now;
            }

            if (jobs.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return jobs.Count;
        }
    }

    public async Task<bool> UpdateJobStatusAsync(
        Guid jobId,
        string leaseToken,
        string newStatus,
        string? providerMessageId = null,
        string? errorCode = null,
        string? errorDetail = null,
        DateTimeOffset? nextAttemptAt = null,
        bool resetDispatchStartedAt = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var shouldResetDispatch = resetDispatchStartedAt || newStatus == "RetryScheduled";

        if (_dbContext.Database.IsNpgsql())
        {
            var rows = await _dbContext.DeliveryJobs
                .Where(j => j.Id == jobId && j.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, newStatus)
                    .SetProperty(j => j.ProviderMessageId, j => providerMessageId ?? j.ProviderMessageId)
                    .SetProperty(j => j.LastErrorCode, j => errorCode ?? j.LastErrorCode)
                    .SetProperty(j => j.LastErrorDetail, j => errorDetail ?? j.LastErrorDetail)
                    .SetProperty(j => j.NextAttemptAt, j => nextAttemptAt ?? j.NextAttemptAt)
                    .SetProperty(j => j.DispatchStartedAt, j => shouldResetDispatch ? (DateTimeOffset?)null : j.DispatchStartedAt)
                    .SetProperty(j => j.SentAt, j => newStatus == "Succeeded" ? now : j.SentAt)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.UpdatedAt, now),
                    cancellationToken);

            return rows > 0;
        }
        else
        {
            var job = await _dbContext.DeliveryJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.LeaseToken == leaseToken, cancellationToken);

            if (job == null)
            {
                return false;
            }

            job.Status = newStatus;
            if (providerMessageId != null) job.ProviderMessageId = providerMessageId;
            if (errorCode != null) job.LastErrorCode = errorCode;
            if (errorDetail != null) job.LastErrorDetail = errorDetail;
            if (nextAttemptAt != null) job.NextAttemptAt = nextAttemptAt.Value;
            if (shouldResetDispatch) job.DispatchStartedAt = null;
            if (newStatus == "Succeeded") job.SentAt = now;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            job.UpdatedAt = now;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> RecordDispatchStartAsync(Guid jobId, string leaseToken, DateTimeOffset dispatchStartedAt, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            var rows = await _dbContext.DeliveryJobs
                .Where(j => j.Id == jobId && j.LeaseToken == leaseToken && j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt > dispatchStartedAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.DispatchStartedAt, dispatchStartedAt)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);

            return rows > 0;
        }
        else
        {
            var job = await _dbContext.DeliveryJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.LeaseToken == leaseToken && j.Status == "Processing" && j.LeaseExpiresAt != null && j.LeaseExpiresAt > dispatchStartedAt, cancellationToken);

            if (job == null)
            {
                return false;
            }

            job.DispatchStartedAt = dispatchStartedAt;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
