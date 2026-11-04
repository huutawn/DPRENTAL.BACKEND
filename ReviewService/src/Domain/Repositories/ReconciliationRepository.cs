using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai IReconciliationRepository truy cập bảng reconciliation_checkpoints qua DbContext.
/// </summary>
public sealed class ReconciliationRepository : IReconciliationRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public ReconciliationRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<ReconciliationCheckpoint?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.ReconciliationCheckpoints
            .OrderByDescending(c => c.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddAsync(ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await _dbContext.ReconciliationCheckpoints.AddAsync(checkpoint, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _dbContext.ReconciliationCheckpoints.Update(checkpoint);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
