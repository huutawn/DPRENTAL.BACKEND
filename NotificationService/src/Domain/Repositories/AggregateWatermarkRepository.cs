using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class AggregateWatermarkRepository : IAggregateWatermarkRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public AggregateWatermarkRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<NotificationAggregateWatermark?> GetWatermarkAsync(Guid aggregateId, Guid recipientUserId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.AggregateId == aggregateId && w.RecipientUserId == recipientUserId, cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationAggregateWatermark>> GetWatermarksForAggregateAsync(Guid aggregateId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AggregateWatermarks
            .AsNoTracking()
            .Where(w => w.AggregateId == aggregateId)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertWatermarkAsync(NotificationAggregateWatermark watermark, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);

        var existing = await _dbContext.AggregateWatermarks
            .FirstOrDefaultAsync(w => w.AggregateId == watermark.AggregateId && w.RecipientUserId == watermark.RecipientUserId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (existing == null)
        {
            watermark.UpdatedAt = now;
            await _dbContext.AggregateWatermarks.AddAsync(watermark, cancellationToken);
        }
        else
        {
            if (watermark.LastOrderVersion > existing.LastOrderVersion)
            {
                existing.LastOrderVersion = watermark.LastOrderVersion;
                existing.LastMessageId = watermark.LastMessageId;
            }

            if (watermark.IsTerminal)
            {
                existing.IsTerminal = true;
                existing.TerminalState = watermark.TerminalState;
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
        CancellationToken cancellationToken = default)
    {
        var recipientList = recipientUserIds.Distinct().ToList();
        if (recipientList.Count == 0)
        {
            return;
        }

        var existingList = await _dbContext.AggregateWatermarks
            .Where(w => w.AggregateId == aggregateId && recipientList.Contains(w.RecipientUserId))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        foreach (var recipientId in recipientList)
        {
            var existing = existingList.FirstOrDefault(w => w.RecipientUserId == recipientId);
            if (existing == null)
            {
                await _dbContext.AggregateWatermarks.AddAsync(new NotificationAggregateWatermark
                {
                    AggregateId = aggregateId,
                    RecipientUserId = recipientId,
                    LastOrderVersion = orderVersion,
                    LastMessageId = messageId,
                    IsTerminal = isTerminal,
                    TerminalState = terminalState,
                    UpdatedAt = now
                }, cancellationToken);
            }
            else
            {
                if (orderVersion > existing.LastOrderVersion)
                {
                    existing.LastOrderVersion = orderVersion;
                    existing.LastMessageId = messageId;
                }

                if (isTerminal)
                {
                    existing.IsTerminal = true;
                    existing.TerminalState = terminalState;
                }

                existing.UpdatedAt = now;
            }
        }
    }
}
