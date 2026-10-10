using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class OperationReceiptRepository : IOperationReceiptRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public OperationReceiptRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalOperationReceipt?> GetByOperationIdAsync(Guid orderId, string operationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOperationReceipts
            .FirstOrDefaultAsync(r => r.RentalOrderId == orderId && r.OperationId == operationId, cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOperationReceipt>> GetPendingOperationsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOperationReceipts
            .Include(r => r.RentalOrder)
                .ThenInclude(o => o!.Items)
            .Where(r => (r.Status == "Pending" || r.Status == "Processing") &&
                        r.RetryCount < 10 &&
                        (r.NextRetryAt == null || r.NextRetryAt <= utcNow))
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _dbContext.RentalOperationReceipts.AddAsync(receipt, cancellationToken);
    }

    public Task UpdateAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _dbContext.RentalOperationReceipts.Update(receipt);
        return Task.CompletedTask;
    }
}
