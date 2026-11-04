using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai EF Core cho IIdempotencyRepository.
/// </summary>
public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public IdempotencyRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IdempotencyRecord?> GetAsync(
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        return await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ActorId == actorId && r.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    public Task<IdempotencyRecord?> GetAsync(
        string idempotencyKey,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        return GetAsync(actorId, idempotencyKey, cancellationToken);
    }

    public async Task<IdempotencyRecord?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        return await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var existing = await _dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => (r.ActorId == record.ActorId && r.IdempotencyKey == record.IdempotencyKey) || r.Id == record.Id,
                cancellationToken);

        if (existing == null)
        {
            await _dbContext.IdempotencyRecords.AddAsync(record, cancellationToken);
        }
        else
        {
            _dbContext.Entry(existing).CurrentValues.SetValues(record);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
