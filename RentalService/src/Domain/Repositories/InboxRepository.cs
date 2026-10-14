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
    public async Task<RentalInboxMessage?> GetByIdForProcessingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalInboxMessages
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<InboxMessageDescriptor?> GetDescriptorByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalInboxMessages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new InboxMessageDescriptor(
                m.Id,
                m.MessageId,
                m.MessageType,
                m.AggregateId,
                m.Status,
                m.RetryCount,
                m.NextRetryAt,
                m.ReceivedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InboxMessageDescriptor>> GetPendingRetryDescriptorsAsync(
        DateTimeOffset utcNow,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalInboxMessages
            .AsNoTracking()
            .Where(m => (m.Status == RentalInboxMessage.StatusReceived || m.Status == RentalInboxMessage.StatusFailed) &&
                        m.RetryCount < 10 &&
                        (m.NextRetryAt == null || m.NextRetryAt <= utcNow))
            .OrderBy(m => m.ReceivedAt)
            .Take(batchSize)
            .Select(m => new InboxMessageDescriptor(
                m.Id,
                m.MessageId,
                m.MessageType,
                m.AggregateId,
                m.Status,
                m.RetryCount,
                m.NextRetryAt,
                m.ReceivedAt))
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
        var entry = _dbContext.Entry(message);
        if (entry.State == EntityState.Detached)
        {
            _dbContext.RentalInboxMessages.Update(message);
        }
    }
}
