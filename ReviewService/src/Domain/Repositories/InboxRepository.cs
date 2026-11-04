using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai IInboxRepository truy cập bảng inbox_messages qua DbContext.
/// </summary>
public sealed class InboxRepository : IInboxRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public InboxRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<bool> ExistsAsync(Guid messageId, string consumerGroup, CancellationToken cancellationToken = default)
    {
        return _dbContext.InboxMessages
            .AnyAsync(m => m.MessageId == messageId && m.ConsumerGroup == consumerGroup, cancellationToken);
    }

    public async Task AddAsync(InboxMessage inboxMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inboxMessage);
        await _dbContext.InboxMessages.AddAsync(inboxMessage, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
