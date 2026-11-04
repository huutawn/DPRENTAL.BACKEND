using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.ReviewService.Domain.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public OutboxRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _dbContext.OutboxMessages.AddAsync(message, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetUnprocessedMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        string claimOwner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return [];
        }

        var expiry = nowUtc.Add(leaseDuration);

        if (_dbContext.Database.IsRelational())
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var sql = """
                SELECT id FROM outbox_messages
                WHERE delivery_state NOT IN ('Published', 'Parked')
                  AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                  AND (lease_expires_at IS NULL OR lease_expires_at <= @now)
                ORDER BY created_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
                """;

            var nowParam = new NpgsqlParameter("now", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = nowUtc.ToUniversalTime() };
            var batchSizeParam = new NpgsqlParameter("batchSize", NpgsqlTypes.NpgsqlDbType.Integer) { Value = batchSize };

            var ids = await _dbContext.Database
                .SqlQueryRaw<Guid>(sql, nowParam, batchSizeParam)
                .ToListAsync(cancellationToken);

            if (ids.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return [];
            }

            var messages = await _dbContext.OutboxMessages
                .Where(m => ids.Contains(m.Id))
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(cancellationToken);

            foreach (var msg in messages)
            {
                var claimToken = Guid.NewGuid();
                msg.Claim(claimOwner, expiry, claimToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return messages;
        }
        else
        {
            var messages = await _dbContext.OutboxMessages
                .Where(m => m.DeliveryState != "Published" &&
                            m.DeliveryState != "Parked" &&
                            (m.NextAttemptAt == null || m.NextAttemptAt <= nowUtc) &&
                            (m.LeaseExpiresAt == null || m.LeaseExpiresAt <= nowUtc))
                .OrderBy(m => m.CreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var msg in messages)
            {
                var claimToken = Guid.NewGuid();
                msg.Claim(claimOwner, expiry, claimToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return messages;
        }
    }

    public async Task<bool> MarkProcessedAsync(
        Guid messageId,
        Guid claimToken,
        string claimOwner,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var utc = nowUtc.ToUniversalTime();

        if (_dbContext.Database.IsRelational())
        {
            int rows = await _dbContext.OutboxMessages
                .Where(m => m.Id == messageId &&
                            m.ClaimToken == claimToken &&
                            m.ClaimOwner == claimOwner &&
                            m.LeaseExpiresAt != null && m.LeaseExpiresAt > utc &&
                            m.DeliveryState != "Published" &&
                            m.DeliveryState != "Parked")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.DeliveryState, "Published")
                    .SetProperty(m => m.ProcessedAt, utc)
                    .SetProperty(m => m.ClaimToken, (Guid?)null)
                    .SetProperty(m => m.ClaimOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken);

            return rows > 0;
        }

        var message = await _dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId &&
                                      m.ClaimToken == claimToken &&
                                      m.ClaimOwner == claimOwner &&
                                      m.LeaseExpiresAt != null && m.LeaseExpiresAt > utc &&
                                      m.DeliveryState != "Published" &&
                                      m.DeliveryState != "Parked",
                                      cancellationToken);

        if (message != null)
        {
            message.MarkProcessed(utc);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> FailAttemptAsync(
        Guid messageId,
        Guid claimToken,
        string claimOwner,
        string errorCode,
        DateTimeOffset nextAttempt,
        bool park,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var utc = nowUtc.ToUniversalTime();
        var next = nextAttempt.ToUniversalTime();

        if (_dbContext.Database.IsRelational())
        {
            int rows = await _dbContext.OutboxMessages
                .Where(m => m.Id == messageId &&
                            m.ClaimToken == claimToken &&
                            m.ClaimOwner == claimOwner &&
                            m.LeaseExpiresAt != null && m.LeaseExpiresAt > utc &&
                            m.DeliveryState != "Published" &&
                            m.DeliveryState != "Parked")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                    .SetProperty(m => m.DeliveryState, m => (park || m.Attempts + 1 >= 5) ? "Parked" : "Pending")
                    .SetProperty(m => m.LastErrorCode, errorCode)
                    .SetProperty(m => m.NextAttemptAt, next)
                    .SetProperty(m => m.ClaimToken, (Guid?)null)
                    .SetProperty(m => m.ClaimOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null),
                    cancellationToken);

            return rows > 0;
        }

        var message = await _dbContext.OutboxMessages
            .FirstOrDefaultAsync(m => m.Id == messageId &&
                                      m.ClaimToken == claimToken &&
                                      m.ClaimOwner == claimOwner &&
                                      m.LeaseExpiresAt != null && m.LeaseExpiresAt > utc &&
                                      m.DeliveryState != "Published" &&
                                      m.DeliveryState != "Parked",
                                      cancellationToken);

        if (message != null)
        {
            message.FailAttempt(errorCode, next, park);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }
}
