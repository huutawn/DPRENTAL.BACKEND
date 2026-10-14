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
                    SELECT * FROM rental.rental_outbox_messages
                    WHERE ""Status"" = 'Pending'
                      AND (""ScheduledAt"" IS NULL OR ""ScheduledAt"" <= NOW())
                      AND (""LockedUntilUtc"" IS NULL OR ""LockedUntilUtc"" <= NOW())
                    ORDER BY ""CreatedAt""
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);

            if (messages.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            var message = messages[0];
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_outbox_messages
                SET ""LockedBy"" = {claimToken},
                    ""LockedUntilUtc"" = NOW() + ({leaseDuration.TotalSeconds} * interval '1 second')
                WHERE ""Id"" = {message.Id}", cancellationToken);

            message.Claim(claimToken, leaseUntil);
            await tx.CommitAsync(cancellationToken);
            return message;
        }
        else
        {
            var message = await _dbContext.RentalOutboxMessages
                .Where(m => m.Status == "Pending" &&
                            (m.ScheduledAt == null || m.ScheduledAt <= utcNow) &&
                            (m.LockedUntilUtc == null || m.LockedUntilUtc <= utcNow))
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (message != null)
            {
                message.Claim(claimToken, leaseUntil);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return message;
        }
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

    public async Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(
        RentalOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        bool supportsTransactions = _dbContext.Database.IsRelational();
        bool hasCallerTx = supportsTransactions && _dbContext.Database.CurrentTransaction != null;

        if (hasCallerTx || !supportsTransactions)
        {
            var existing = await _dbContext.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                if (IsSameContent(existing, message))
                {
                    return new OutboxEnqueueResult(existing, IsReplay: true);
                }

                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT", $"MessageId '{message.Id}' đã tồn tại với nội dung mâu thuẫn.");
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

                if (concurrent != null && IsSameContent(concurrent, message))
                {
                    return new OutboxEnqueueResult(concurrent, IsReplay: true);
                }

                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT", $"MessageId '{message.Id}' đã tồn tại với nội dung mâu thuẫn.");
            }
        }
        else
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var existing = await _dbContext.RentalOutboxMessages
                    .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

                if (existing != null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    if (IsSameContent(existing, message))
                    {
                        return new OutboxEnqueueResult(existing, IsReplay: true);
                    }

                    return Error.Conflict("OUTBOX_MESSAGE_CONFLICT", $"MessageId '{message.Id}' đã tồn tại với nội dung mâu thuẫn.");
                }

                await _dbContext.RentalOutboxMessages.AddAsync(message, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new OutboxEnqueueResult(message, IsReplay: false);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await tx.RollbackAsync(cancellationToken);
                _dbContext.Entry(message).State = EntityState.Detached;

                var concurrent = await _dbContext.RentalOutboxMessages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

                if (concurrent != null && IsSameContent(concurrent, message))
                {
                    return new OutboxEnqueueResult(concurrent, IsReplay: true);
                }

                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT", $"MessageId '{message.Id}' đã tồn tại với nội dung mâu thuẫn.");
            }
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

    private static bool IsSameContent(RentalOutboxMessage a, RentalOutboxMessage b)
    {
        if (!string.Equals(a.Topic, b.Topic, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(a.PartitionKey, b.PartitionKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(a.MessageType, b.MessageType, StringComparison.Ordinal))
        {
            return false;
        }

        return CanonicalJson.AreEquivalent(a.Payload, b.Payload);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
        {
            return true;
        }

        string msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("PK_", StringComparison.OrdinalIgnoreCase);
    }
}
