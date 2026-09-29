using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfMediaOutboxRepository(AuthDbContext dbContext) : IMediaOutboxRepository
{
    public async Task AddAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await dbContext.MediaOutboxMessages.AddAsync(message, cancellationToken);
        // The owning user/organization repository saves this message with its aggregate.
    }

    public async Task<IReadOnlyList<MediaOutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        List<MediaOutboxMessage> messages;
        if (dbContext.Database.IsRelational())
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            messages = await dbContext.MediaOutboxMessages.FromSqlInterpolated($"""
                SELECT * FROM auth."MediaOutboxMessages"
                WHERE (("Status" = 'Pending' AND ("NextRetryAtUtc" IS NULL OR "NextRetryAtUtc" <= {now}))
                    OR ("Status" = 'Processing' AND "NextRetryAtUtc" <= {now}))
                    AND "RetryCount" < 5
                ORDER BY "CreatedAtUtc", "Id"
                LIMIT {batchSize} FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken);
            foreach (MediaOutboxMessage message in messages)
            {
                message.Status = "Processing";
                message.NextRetryAtUtc = now.AddMinutes(1);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            messages = await dbContext.MediaOutboxMessages
                .Where(m => ((m.Status == "Pending" && (m.NextRetryAtUtc == null || m.NextRetryAtUtc <= now)) ||
                             (m.Status == "Processing" && m.NextRetryAtUtc <= now)) && m.RetryCount < 5)
                .OrderBy(m => m.CreatedAtUtc)
                .ThenBy(m => m.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            foreach (MediaOutboxMessage message in messages)
            {
                message.Status = "Processing";
                message.NextRetryAtUtc = now.AddMinutes(1);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return messages;
    }

    public async Task UpdateAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        dbContext.MediaOutboxMessages.Update(message);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
