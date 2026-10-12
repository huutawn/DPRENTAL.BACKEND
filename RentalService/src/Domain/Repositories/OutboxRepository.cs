using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public OutboxRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<RentalOutboxMessage>> ClaimPendingBatchAsync(
        string claimToken,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        DateTimeOffset leaseUntil = utcNow.Add(leaseDuration);

        if (_dbContext.Database.IsNpgsql())
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var messages = await _dbContext.RentalOutboxMessages
                .FromSqlInterpolated($@"
                    SELECT * FROM rental.rental_outbox_messages
                    WHERE ""Status"" = 'Pending'
                      AND (""ScheduledAt"" IS NULL OR ""ScheduledAt"" <= NOW())
                      AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" <= NOW())
                    ORDER BY ""CreatedAt""
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);

            if (messages.Count > 0)
            {
                var ids = messages.Select(m => m.Id).ToList();
                await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE rental.rental_outbox_messages
                    SET ""LockedBy"" = {claimToken},
                        ""LockedUntilUtc"" = NOW() + ({leaseDuration.TotalSeconds} * interval '1 second')
                    WHERE ""Id"" = ANY({ids})", cancellationToken);

                foreach (var msg in messages)
                {
                    msg.Claim(claimToken, leaseUntil);
                }
            }
            await tx.CommitAsync(cancellationToken);
            return messages;
        }
        else
        {
            var messages = await _dbContext.RentalOutboxMessages
                .Where(m => m.Status == "Pending" &&
                            (m.ScheduledAt == null || m.ScheduledAt <= utcNow) &&
                            (m.LockedUntilUtc == null || m.LockedUntilUtc <= utcNow))
                .OrderBy(m => m.CreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var msg in messages)
            {
                msg.Claim(claimToken, leaseUntil);
            }

            if (messages.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return messages;
        }
    }

    public async Task AddAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _dbContext.RentalOutboxMessages.AddAsync(message, cancellationToken);
    }

    public async Task<bool> MarkPublishedAsync(
        Guid id,
        string claimToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_outbox_messages
                SET ""Status"" = 'Published',
                    ""PublishedAt"" = {publishedAt},
                    ""LockedBy"" = NULL,
                    ""LockedUntilUtc"" = NULL
                WHERE ""Id"" = {id}
                  AND ""Status"" = 'Pending'
                  AND ""LockedBy"" = {claimToken}
                  AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" > NOW())", cancellationToken);

            return rows == 1;
        }
        else
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            var msg = await _dbContext.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.Id == id && m.LockedBy == claimToken && m.Status == "Pending" && (m.LockedUntilUtc == null || m.LockedUntilUtc >= utcNow), cancellationToken);

            if (msg == null)
            {
                return false;
            }

            msg.MarkPublished(publishedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> RecordFailureAsync(
        Guid id,
        string claimToken,
        string errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextScheduledAt = null,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_outbox_messages
                SET ""RetryCount"" = ""RetryCount"" + 1,
                    ""Status"" = CASE WHEN ""RetryCount"" + 1 >= 10 THEN 'DeadLettered' ELSE 'Pending' END,
                    ""ErrorMessage"" = {errorMessage},
                    ""LastAttemptedAt"" = {attemptedAt},
                    ""ScheduledAt"" = CASE WHEN ""RetryCount"" + 1 >= 10 THEN NULL ELSE {nextScheduledAt} END,
                    ""LockedBy"" = NULL,
                    ""LockedUntilUtc"" = NULL
                WHERE ""Id"" = {id}
                  AND ""Status"" = 'Pending'
                  AND ""LockedBy"" = {claimToken}
                  AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" > NOW())", cancellationToken);

            return rows == 1;
        }
        else
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            var msg = await _dbContext.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.Id == id && m.LockedBy == claimToken && m.Status == "Pending" && (m.LockedUntilUtc == null || m.LockedUntilUtc >= utcNow), cancellationToken);

            if (msg == null)
            {
                return false;
            }

            msg.RecordFailure(errorMessage, attemptedAt, nextScheduledAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
