using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Infrastructure.Repositories;

public sealed class EfOutboxRepository : IOutboxRepository
{
    private readonly PaymentServiceDbContext _dbContext;

    public EfOutboxRepository(PaymentServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        string workerId,
        DateTimeOffset utcNow,
        TimeSpan leaseDuration,
        int batchSize,
        int maxRetries = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var candidates = await _dbContext.OutboxMessages
            .FromSql($"""
                SELECT m.*
                FROM outbox_messages m
                WHERE m."PublishedAt" IS NULL
                  AND (m."LockedUntilUtc" IS NULL OR m."LockedUntilUtc" < {utcNow})
                  AND (m."NextRetryAt" IS NULL OR m."NextRetryAt" <= {utcNow})
                  AND m."RetryCount" < {maxRetries}
                  AND NOT EXISTS (
                      SELECT 1 FROM outbox_messages earlier
                      WHERE earlier."AggregateId" = m."AggregateId"
                        AND earlier."PublishedAt" IS NULL
                        AND (earlier."CreatedAt", earlier."Id") < (m."CreatedAt", m."Id")
                  )
                ORDER BY m."CreatedAt" ASC, m."Id" ASC
                LIMIT {batchSize}
                FOR UPDATE OF m SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return [];
        }

        var leaseExpiry = utcNow.Add(leaseDuration);
        foreach (var message in candidates)
        {
            message.Claim(workerId, leaseExpiry);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return candidates;
    }

    public async Task<bool> MarkPublishedAsync(
        Guid id,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        int affected = await _dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE outbox_messages
            SET "PublishedAt" = {publishedAt},
                "LockedBy" = NULL,
                "LockedUntilUtc" = NULL,
                "LastErrorMessage" = NULL
            WHERE "Id" = {id}
              AND "LockedBy" = {workerId}
              AND "LockedUntilUtc" >= {publishedAt}
            """,
            cancellationToken);

        return affected > 0;
    }

    public async Task RecordFailureAsync(
        Guid id,
        string workerId,
        string errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextRetryAt,
        bool isExhausted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        if (isExhausted)
        {
            await _dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE outbox_messages
                SET "LastErrorMessage" = {errorMessage},
                    "NextRetryAt" = NULL,
                    "LockedBy" = NULL,
                    "LockedUntilUtc" = NULL
                WHERE "Id" = {id} AND "LockedBy" = {workerId}
                """,
                cancellationToken);
        }
        else
        {
            await _dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE outbox_messages
                SET "LastErrorMessage" = {errorMessage},
                    "NextRetryAt" = {nextRetryAt},
                    "LockedBy" = NULL,
                    "LockedUntilUtc" = NULL
                WHERE "Id" = {id} AND "LockedBy" = {workerId}
                """,
                cancellationToken);
        }
    }
}
