using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store quản lý Outbox messages với FOR UPDATE SKIP LOCKED và lease tokens trên PostgreSQL.
/// </summary>
public sealed class OutboxStore : IOutboxStore
{
    private readonly InventoryServiceDbContext _context;

    public OutboxStore(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public void Add(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.OutboxMessages.Add(message);
    }

    /// <inheritdoc />
    public async Task<OutboxMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) return [];

        var leaseExpiresAt = now.Add(leaseDuration);

        if (_context.Database.IsNpgsql())
        {
            // Transaction ngắn với FOR UPDATE SKIP LOCKED và CTE claim atomic
            return await _context.OutboxMessages
                .FromSqlInterpolated($"""
                    WITH eligible AS (
                        SELECT o.id
                        FROM inventory.outbox_messages o
                        WHERE o.published_at IS NULL
                          AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= {now})
                          AND (o.lock_lease_expires_at IS NULL OR o.lock_lease_expires_at <= {now})
                          AND o.retry_count < 10
                          AND NOT EXISTS (
                              SELECT 1
                              FROM inventory.outbox_messages prev
                              WHERE prev.kafka_key = o.kafka_key
                                AND prev.published_at IS NULL
                                AND (prev.created_at < o.created_at OR (prev.created_at = o.created_at AND prev.id < o.id))
                          )
                        ORDER BY o.created_at, o.id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    ),
                    claimed AS (
                        UPDATE inventory.outbox_messages o
                        SET lock_lease_token = {leaseToken},
                            lock_lease_expires_at = {leaseExpiresAt}
                        FROM eligible
                        WHERE o.id = eligible.id
                        RETURNING o.*
                    )
                    SELECT * FROM claimed;
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        // Fallback cho môi trường test InMemory
        var candidates = await _context.OutboxMessages
            .Where(o => o.PublishedAt == null &&
                        (o.NextAttemptAt == null || o.NextAttemptAt <= now) &&
                        (o.LockLeaseExpiresAt == null || o.LockLeaseExpiresAt <= now) &&
                        o.RetryCount < 10)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);

        var allUnpublished = await _context.OutboxMessages
            .Where(o => o.PublishedAt == null)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);

        var earliestPerKey = allUnpublished.GroupBy(x => x.KafkaKey).ToDictionary(g => g.Key, g => g.First().Id);
        var claimed = new List<OutboxMessage>();

        foreach (var candidate in candidates)
        {
            if (claimed.Count >= batchSize) break;
            if (earliestPerKey.TryGetValue(candidate.KafkaKey, out var earliestId) && earliestId == candidate.Id)
            {
                candidate.AssignLease(leaseToken, leaseExpiresAt);
                claimed.Add(candidate);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return claimed;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsPublishedAsync(
        Guid id,
        Guid leaseToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.outbox_messages
                SET published_at = {publishedAt},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL,
                    last_error = NULL
                WHERE id = {id} AND lock_lease_token = {leaseToken}
                """, cancellationToken);
            return rows > 0;
        }

        var msg = await _context.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id && m.LockLeaseToken == leaseToken, cancellationToken);
        if (msg is null) return false;
        msg.MarkAsPublished(publishedAt);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RecordFailureAsync(
        Guid id,
        Guid leaseToken,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset? leaseExpiry = null,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.outbox_messages
                SET retry_count = retry_count + 1,
                    last_error = {errorMessage},
                    next_attempt_at = {nextAttemptAt},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = {leaseExpiry}
                WHERE id = {id} AND lock_lease_token = {leaseToken}
                """, cancellationToken);
            return rows > 0;
        }

        var msg = await _context.OutboxMessages.FirstOrDefaultAsync(m => m.Id == id && m.LockLeaseToken == leaseToken, cancellationToken);
        if (msg is null) return false;
        msg.RecordFailure(errorMessage, nextAttemptAt, leaseExpiry);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
    /// <inheritdoc />
    public async Task<bool> RequeueAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var rows = await _context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.outbox_messages
                SET retry_count = 0,
                    last_error = NULL,
                    next_attempt_at = {now},
                    lock_lease_token = NULL,
                    lock_lease_expires_at = NULL
                WHERE id = {id}
                """, cancellationToken);
            return rows > 0;
        }

        var msg = await _context.OutboxMessages.FindAsync([id], cancellationToken);
        if (msg is null) return false;
        msg.Requeue(now);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
