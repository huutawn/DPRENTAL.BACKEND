using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class InboxRepository : IInboxRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public InboxRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalInboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalInboxMessages
            .FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken);
    }

    public async Task<IReadOnlyList<RentalInboxMessage>> GetPendingRetryBatchAsync(
        DateTimeOffset utcNow,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalInboxMessages
            .Where(m => (m.Status == "Received" || m.Status == "Failed") &&
                        m.RetryCount < 10 &&
                        (m.NextRetryAt == null || m.NextRetryAt <= utcNow))
            .OrderBy(m => m.ReceivedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RentalInboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _dbContext.RentalInboxMessages.AddAsync(message, cancellationToken);
    }

    public void Update(RentalInboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _dbContext.RentalInboxMessages.Update(message);
    }
}
