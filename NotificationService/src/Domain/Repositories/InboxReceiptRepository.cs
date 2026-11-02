using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class InboxReceiptRepository : IInboxReceiptRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public InboxReceiptRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<bool> ExistsAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.InboxReceipts
            .AsNoTracking()
            .AnyAsync(r => r.ConsumerName == consumerName && r.MessageId == messageId, cancellationToken);
    }

    public async Task AddAsync(NotificationInboxReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _dbContext.InboxReceipts.AddAsync(receipt, cancellationToken);
    }
}
