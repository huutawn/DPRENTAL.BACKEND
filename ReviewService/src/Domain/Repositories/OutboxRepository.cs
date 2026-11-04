using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public OutboxRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
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

    public async Task MarkProcessedAsync(Guid messageId, DateTimeOffset processedAt, CancellationToken cancellationToken = default)
    {
        var message = await _dbContext.OutboxMessages.FindAsync([messageId], cancellationToken);
        if (message != null)
        {
            message.MarkProcessed(processedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        string claimOwner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var messages = await _dbContext.OutboxMessages
            .Where(m => m.DeliveryState != "Published" &&
                        m.DeliveryState != "Parked" &&
                        (m.NextAttemptAt == null || m.NextAttemptAt <= nowUtc) &&
                        (m.LeaseExpiresAt == null || m.LeaseExpiresAt <= nowUtc))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var expiry = nowUtc.Add(leaseDuration);
        foreach (var msg in messages)
        {
            msg.Claim(claimOwner, expiry);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return messages;
    }

    public async Task FailAttemptAsync(
        Guid messageId,
        string errorCode,
        DateTimeOffset nextAttempt,
        bool park = false,
        CancellationToken cancellationToken = default)
    {
        var message = await _dbContext.OutboxMessages.FindAsync([messageId], cancellationToken);
        if (message != null)
        {
            message.FailAttempt(errorCode, nextAttempt, park);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
