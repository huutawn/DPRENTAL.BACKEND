using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class AggregateWatermarkRepository : IAggregateWatermarkRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public AggregateWatermarkRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AcquireAdvisoryLockAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            var parameter = new NpgsqlParameter("@orderId", orderId.ToString());
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@orderId::text));",
                new object[] { parameter },
                cancellationToken);
        }
    }

    public async Task<NotificationAggregateWatermark?> GetWatermarkAsync(
        string scope,
        Guid aggregateId,
        Guid recipientUserId,
        CancellationToken cancellationToken = default)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Scope == effectiveScope && w.AggregateId == aggregateId && w.RecipientUserId == recipientUserId, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(
        string scope,
        Guid aggregateId,
        CancellationToken cancellationToken = default)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .Where(w => w.Scope == effectiveScope && w.AggregateId == aggregateId)
            .ToListAsync(cancellationToken);
    }

    public async Task<AggregateStateProjection?> GetAggregateStateAsync(
        string scope,
        Guid aggregateId,
        CancellationToken cancellationToken = default)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? NotificationWatermarkScope.RentalOrder : scope;

        var watermarks = await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .Where(w => w.Scope == effectiveScope && w.AggregateId == aggregateId)
            .ToListAsync(cancellationToken);

        if (watermarks.Count == 0)
        {
            return null;
        }

        var maxVersion = watermarks.Max(w => w.LastOrderVersion);
        var isPaid = watermarks.Any(w => w.IsPaid || string.Equals(w.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase));

        var terminalWatermarks = watermarks
            .Where(w => w.IsTerminal && !string.IsNullOrWhiteSpace(w.TerminalState) && !string.Equals(w.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool isTerminal;
        string? terminalState = null;
        long? terminalVersion = null;

        if (terminalWatermarks.Count > 0)
        {
            var highestTerminalVersion = terminalWatermarks.Max(w => w.LastOrderVersion);
            var highestTerminalRecords = terminalWatermarks
                .Where(w => w.LastOrderVersion == highestTerminalVersion)
                .ToList();

            var distinctStates = highestTerminalRecords
                .Select(w => w.TerminalState!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctStates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Data integrity error: Conflicting terminal states ({string.Join(", ", distinctStates)}) found for aggregate {aggregateId} in scope {effectiveScope} at version {highestTerminalVersion}.");
            }

            isTerminal = true;
            terminalState = highestTerminalRecords[0].TerminalState;
            terminalVersion = highestTerminalVersion;
        }
        else
        {
            isTerminal = false;
        }

        return new AggregateStateProjection(
            Scope: effectiveScope,
            AggregateId: aggregateId,
            MaxVersion: maxVersion,
            IsPaid: isPaid,
            IsTerminal: isTerminal,
            TerminalState: terminalState,
            TerminalVersion: terminalVersion);
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

            if (watermark.IsPaid)
            {
                existing.IsPaid = true;
            }

            if (watermark.ParentOrderId.HasValue)
            {
                existing.ParentOrderId = watermark.ParentOrderId.Value;
            }

            if (watermark.ParentItemId.HasValue)
            {
                existing.ParentItemId = watermark.ParentItemId.Value;
            }
            existing.UpdatedAt = now;
        }
    }

    public async Task UpsertWatermarksForRecipientsAsync(
        string scope,
        Guid aggregateId,
        IEnumerable<Guid> recipientUserIds,
        long orderVersion,
        string? messageId,
        bool isTerminal,
        string? terminalState,
        bool isPaid = false,
        Guid? parentOrderId = null,
        Guid? parentItemId = null,
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
                    ParentOrderId = parentOrderId,
                    ParentItemId = parentItemId,
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

                if (isPaid)
                {
                    existing.IsPaid = true;
                }

                if (parentOrderId.HasValue)
                {
                    existing.ParentOrderId = parentOrderId.Value;
                }

                if (parentItemId.HasValue)
                {
                    existing.ParentItemId = parentItemId.Value;
                }
                existing.UpdatedAt = now;
            }
        }
    }
}
