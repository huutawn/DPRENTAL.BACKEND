using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store quản lý phiên saga phân tán bằng EF Core trên PostgreSQL.
/// </summary>
public sealed class SagaStore : ISagaStore
{
    private readonly InventoryServiceDbContext _context;

    public SagaStore(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<SagaInstance?> GetAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.SagaInstances
            .FirstOrDefaultAsync(
                s => s.SagaType == sagaType && s.OrganizationId == organizationId && s.CorrelationId == correlationId,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SagaInstance?> GetForUpdateAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            return await _context.SagaInstances
                .FromSqlInterpolated($"SELECT *, xmin FROM inventory.saga_instances WHERE saga_type = {sagaType} AND organization_id = {organizationId} AND correlation_id = {correlationId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await GetAsync(sagaType, organizationId, correlationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SagaInstance>> ClaimOverdueTimeoutsAsync(
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
            return await _context.SagaInstances
                .FromSqlInterpolated($"""
                    WITH eligible AS (
                        SELECT id
                        FROM inventory.saga_instances
                        WHERE state NOT IN (3, 5)
                          AND timeout_id IS NOT NULL
                          AND timeout_deadline IS NOT NULL
                          AND timeout_deadline <= {now}
                          AND timeout_retry_count < 5
                          AND (timeout_lease_expires_at IS NULL OR timeout_lease_expires_at <= {now})
                        ORDER BY timeout_deadline, id
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    ),
                    claimed AS (
                        UPDATE inventory.saga_instances s
                        SET timeout_lease_token = {leaseToken},
                            timeout_lease_expires_at = {leaseExpiresAt}
                        FROM eligible
                        WHERE s.id = eligible.id
                        RETURNING s.*, s.xmin
                    )
                    SELECT * FROM claimed;
                    """)
                .ToListAsync(cancellationToken);
        }

        var candidates = await _context.SagaInstances
            .Where(s => s.State != SagaState.Completed && s.State != SagaState.Failed &&
                        s.TimeoutId != null && s.TimeoutDeadline != null && s.TimeoutDeadline <= now &&
                        s.TimeoutRetryCount < 5 &&
                        (s.TimeoutLeaseExpiresAt == null || s.TimeoutLeaseExpiresAt <= now))
            .OrderBy(s => s.TimeoutDeadline)
            .ThenBy(s => s.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var c in candidates)
        {
            c.AssignTimeoutLease(leaseToken, leaseExpiresAt);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return candidates;
    }

    /// <inheritdoc />
    public void Add(SagaInstance saga)
    {
        ArgumentNullException.ThrowIfNull(saga);
        _context.SagaInstances.Add(saga);
    }

    /// <inheritdoc />
    public void Update(SagaInstance saga)
    {
        ArgumentNullException.ThrowIfNull(saga);
        _context.SagaInstances.Update(saga);
    }
}
