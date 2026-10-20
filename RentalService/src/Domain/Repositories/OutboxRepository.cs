using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
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

    public async Task<RentalOutboxMessage?> ClaimNextPendingAsync(
        string claimToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        DateTimeOffset leaseUntil = utcNow.Add(leaseDuration);

        if (_dbContext.Database.IsNpgsql())
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var messages = await _dbContext.RentalOutboxMessages
                .FromSqlInterpolated($@"
                    WITH candidate AS (
                        SELECT m.*
                        FROM rental.rental_outbox_messages m
                        WHERE m.""Status"" = 'Pending'
                          AND (m.""ScheduledAt"" IS NULL OR m.""ScheduledAt"" <= NOW())
                          AND (m.""LockedUntilUtc"" IS NULL OR m.""LockedUntilUtc"" <= NOW())
                          AND NOT EXISTS (
                              SELECT 1 FROM rental.rental_outbox_messages prev
                              WHERE prev.""Topic"" = m.""Topic""
                                AND prev.""PartitionKey"" = m.""PartitionKey""
                                AND prev.""Sequence"" < m.""Sequence""
                                AND prev.""Status"" <> 'Published'
                          )
                        ORDER BY m.""Sequence""
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE rental.rental_outbox_messages u
                    SET ""LockedBy"" = {claimToken},
                        ""LockedUntilUtc"" = NOW() + ({leaseDuration.TotalSeconds} * interval '1 second')
                    FROM candidate
                    WHERE u.""Id"" = candidate.""Id""
                    RETURNING u.*;")
                .ToListAsync(cancellationToken);

            if (messages.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            var message = messages[0];
            message.Claim(claimToken, leaseUntil);
            await tx.CommitAsync(cancellationToken);
            return message;
        }
        else
        {
            var message = await _dbContext.RentalOutboxMessages
                .Where(m => m.Status == "Pending" &&
                            (m.ScheduledAt == null || m.ScheduledAt <= utcNow) &&
                            (m.LockedUntilUtc == null || m.LockedUntilUtc <= utcNow) &&
                            !_dbContext.RentalOutboxMessages.Any(prev =>
                                prev.Topic == m.Topic &&
                                prev.PartitionKey == m.PartitionKey &&
                                prev.Sequence < m.Sequence &&
                                prev.Status != "Published"))
                .OrderBy(m => m.Sequence)
                .FirstOrDefaultAsync(cancellationToken);
            if (message != null)
            {
                message.Claim(claimToken, leaseUntil);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return message;
        }
    }


    public async Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(
        RentalOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_dbContext.Database.IsRelational())
        {
            bool hasCallerTx = _dbContext.Database.CurrentTransaction != null;
            if (hasCallerTx)
            {
                return await ExecuteInsertOnConflictAsync(message, cancellationToken);
            }
            else
            {
                await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var result = await ExecuteInsertOnConflictAsync(message, cancellationToken);
                if (result.IsSuccess)
                {
                    await tx.CommitAsync(cancellationToken);
                }
                else
                {
                    await tx.RollbackAsync(cancellationToken);
                }
                return result;
            }
        }
        else
        {
            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            try
            {
                await _dbContext.RentalOutboxMessages.AddAsync(message, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new OutboxEnqueueResult(message, IsReplay: false);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _dbContext.Entry(message).State = EntityState.Detached;
                var concurrent = await _dbContext.RentalOutboxMessages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

                if (concurrent != null)
                {
                    return new OutboxEnqueueResult(concurrent, IsReplay: true);
                }

                return Error.Failure("OUTBOX_ENQUEUE_FAILED", "Failed to enqueue outbox message concurrently.");
            }
        }
    }

    private async Task<Result<OutboxEnqueueResult>> ExecuteInsertOnConflictAsync(
        RentalOutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            int rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO rental.rental_outbox_messages (
                    ""Id"",
                    ""Topic"",
                    ""PartitionKey"",
                    ""MessageType"",
                    ""Payload"",
                    ""Status"",
                    ""CreatedAt"",
                    ""ScheduledAt"",
                    ""PublishedAt"",
                    ""LastAttemptedAt"",
                    ""LockedBy"",
                    ""LockedUntilUtc"",
                    ""RetryCount"",
                    ""ErrorMessage""
                ) VALUES (
                    {message.Id},
                    {message.Topic},
                    {message.PartitionKey},
                    {message.MessageType},
                    {message.Payload},
                    {message.Status},
                    {message.CreatedAt},
                    {message.ScheduledAt},
                    {message.PublishedAt},
                    {message.LastAttemptedAt},
                    {message.LockedBy},
                    {message.LockedUntilUtc},
                    {message.RetryCount},
                    {message.ErrorMessage}
                )
                ON CONFLICT (""Id"") DO NOTHING;", cancellationToken);

            if (rows > 0)
            {
                return new OutboxEnqueueResult(message, IsReplay: false);
            }

            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            return Error.Failure("OUTBOX_NOT_FOUND_AFTER_CONFLICT", $"MessageId '{message.Id}' had conflict but could not be read.");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            return Error.Failure("OUTBOX_NOT_FOUND_AFTER_CONFLICT", $"MessageId '{message.Id}' had unique violation but could not be read.");
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
        string? targetStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rows;
            if (targetStatus == "DeadLettered")
            {
                rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE rental.rental_outbox_messages
                    SET ""RetryCount"" = ""RetryCount"" + 1,
                        ""Status"" = 'DeadLettered',
                        ""ErrorMessage"" = {errorMessage},
                        ""LastAttemptedAt"" = {attemptedAt},
                        ""ScheduledAt"" = NULL,
                        ""LockedBy"" = NULL,
                        ""LockedUntilUtc"" = NULL
                    WHERE ""Id"" = {id}
                      AND ""Status"" = 'Pending'
                      AND ""LockedBy"" = {claimToken}
                      AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" > NOW())", cancellationToken);
            }
            else if (targetStatus == "Pending")
            {
                rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE rental.rental_outbox_messages
                    SET ""RetryCount"" = ""RetryCount"" + 1,
                        ""Status"" = 'Pending',
                        ""ErrorMessage"" = {errorMessage},
                        ""LastAttemptedAt"" = {attemptedAt},
                        ""ScheduledAt"" = {nextScheduledAt},
                        ""LockedBy"" = NULL,
                        ""LockedUntilUtc"" = NULL
                    WHERE ""Id"" = {id}
                      AND ""Status"" = 'Pending'
                      AND ""LockedBy"" = {claimToken}
                      AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" > NOW())", cancellationToken);
            }
            else
            {
                rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
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
            }

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

            msg.RecordFailure(errorMessage, attemptedAt, nextScheduledAt, targetStatus);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }


    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505";
    }
}
