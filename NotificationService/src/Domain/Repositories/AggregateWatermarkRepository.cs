using System.Collections.Concurrent;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class AggregateWatermarkRepository : IAggregateWatermarkRepository
{
    private readonly NotificationServiceDbContext _dbContext;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InMemorySemaphores = new();
    private static readonly ConcurrentDictionary<Guid, int> InMemoryLockCounts = new();

    public AggregateWatermarkRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public static int GetInMemoryLockCount(Guid aggregateId)
    {
        return InMemoryLockCounts.TryGetValue(aggregateId, out var count) ? count : 0;
    }

    public static void ResetInMemoryLockCounts()
    {
        InMemoryLockCounts.Clear();
    }

    public async Task AcquireAdvisoryLockAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            var parameter = new NpgsqlParameter("@aggregateId", aggregateId.ToString());
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@aggregateId::text));",
                new object[] { parameter },
                cancellationToken);
            return;
        }

        // In-memory provider fallback for unit tests using a lock/semaphore
        var semaphore = InMemorySemaphores.GetOrAdd(aggregateId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            InMemoryLockCounts.AddOrUpdate(aggregateId, 1, (_, c) => c + 1);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        Guid aggregateId,
        Guid recipientUserId,
        string scope = NotificationWatermarkScope.RentalOrder,
        CancellationToken cancellationToken = default)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Scope == effectiveScope && w.AggregateId == aggregateId && w.RecipientUserId == recipientUserId, cancellationToken);
    }

    public Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        string scope,
        Guid aggregateId,
        Guid recipientUserId,
        CancellationToken cancellationToken = default)
    {
        return GetWatermarkAsync(aggregateId, recipientUserId, scope, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        Guid aggregateId,
        string scope = NotificationWatermarkScope.RentalOrder,
        CancellationToken cancellationToken = default)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .Where(w => w.Scope == effectiveScope && w.AggregateId == aggregateId)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        string scope,
        Guid aggregateId,
        CancellationToken cancellationToken = default)
    {
        return GetWatermarksForAggregateAsync(aggregateId, scope, cancellationToken);
    }

    public async Task UpsertWatermarkAsync(NotificationAggregateWatermark watermark, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        var effectiveScope = string.IsNullOrWhiteSpace(watermark.Scope) ? NotificationWatermarkScope.RentalOrder : watermark.Scope;

        var existing = await _dbContext.AggregateWatermarks
            .FirstOrDefaultAsync(w => w.Scope == effectiveScope && w.AggregateId == watermark.AggregateId && w.RecipientUserId == watermark.RecipientUserId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (existing == null)
        {
            watermark.Scope = effectiveScope;
            watermark.UpdatedAt = now;
            await _dbContext.AggregateWatermarks.AddAsync(watermark, cancellationToken);
        }
        else
        {
            if (watermark.LastOrderVersion > existing.LastOrderVersion)
            {
                existing.LastOrderVersion = watermark.LastOrderVersion;
                existing.LastMessageId = watermark.LastMessageId;
                if (watermark.IsTerminal)
                {
                    existing.IsTerminal = true;
                    existing.TerminalState = watermark.TerminalState;
                }
            }
            else if (watermark.LastOrderVersion == existing.LastOrderVersion)
            {
                if (watermark.IsTerminal && !existing.IsTerminal)
                {
                    existing.IsTerminal = true;
                    existing.TerminalState = watermark.TerminalState;
                }
            }
            // Watermark only advances version; older terminal events cannot overwrite newer terminal state.

            if (watermark.IsPaid)
            {
                existing.IsPaid = true;
            }

            existing.UpdatedAt = now;
        }
    }

    public async Task UpsertWatermarksForRecipientsAsync(
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        string scope = NotificationWatermarkScope.RentalOrder,
        bool isPaid = false,
        CancellationToken cancellationToken = default)
    {
        var recipientList = recipientUserIds.Distinct().ToList();
        if (recipientList.Count == 0)
        {
            return;
        }

        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;

        var existingList = await _dbContext.AggregateWatermarks
            .Where(w => w.Scope == effectiveScope && w.AggregateId == aggregateId && recipientList.Contains(w.RecipientUserId))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var recipientId in recipientList)
        {
            var existing = existingList.FirstOrDefault(w => w.RecipientUserId == recipientId);
            if (existing == null)
            {
                await _dbContext.AggregateWatermarks.AddAsync(new NotificationAggregateWatermark
                {
                    Scope = effectiveScope,
                    AggregateId = aggregateId,
                    RecipientUserId = recipientId,
                    LastOrderVersion = orderVersion,
                    LastMessageId = messageId,
                    IsTerminal = isTerminal,
                    TerminalState = terminalState,
                    IsPaid = isPaid,
                    UpdatedAt = now
                }, cancellationToken);
            }
            else
            {
                if (orderVersion > existing.LastOrderVersion)
                {
                    existing.LastOrderVersion = orderVersion;
                    existing.LastMessageId = messageId;
                    if (isTerminal)
                    {
                        existing.IsTerminal = true;
                        existing.TerminalState = terminalState;
                    }
                }
                else if (orderVersion == existing.LastOrderVersion)
                {
                    if (isTerminal && !existing.IsTerminal)
                    {
                        existing.IsTerminal = true;
                        existing.TerminalState = terminalState;
                    }
                }
                // Watermark only advances version; older terminal events cannot overwrite newer terminal state.

                if (isPaid)
                {
                    existing.IsPaid = true;
                }

                existing.UpdatedAt = now;
            }
        }
    }

    public Task UpsertWatermarksForRecipientsAsync(
        string scope,
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        bool isPaid = false,
        CancellationToken cancellationToken = default)
    {
        return UpsertWatermarksForRecipientsAsync(
            aggregateId,
            recipientUserIds,
            orderVersion,
            messageId,
            isTerminal,
            terminalState,
            scope,
            isPaid,
            cancellationToken);
    }

    public Task UpsertWatermarksAsync(
        string scope,
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        bool isPaid = false,
        CancellationToken cancellationToken = default)
    {
        return UpsertWatermarksForRecipientsAsync(
            aggregateId,
            recipientUserIds,
            orderVersion,
            messageId,
            isTerminal,
            terminalState,
            scope,
            isPaid,
            cancellationToken);
    }
}
