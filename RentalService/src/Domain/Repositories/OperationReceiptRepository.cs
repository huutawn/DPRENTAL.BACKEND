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

    public async Task<IReadOnlyList<OperationReceiptDescriptor>> GetPendingOperationDescriptorsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
        => await GetPendingOperationDescriptorsExcludingActionsAsync(utcNow, batchSize, [], cancellationToken);

    public async Task<IReadOnlyList<OperationReceiptDescriptor>> GetPendingOperationDescriptorsExcludingActionsAsync(
        DateTimeOffset utcNow, int batchSize, IReadOnlyCollection<string> excludedActions,
        CancellationToken cancellationToken = default)
    {
        string[] excluded = excludedActions.ToArray();
        return await _dbContext.RentalOperationReceipts
            .AsNoTracking()
            .Where(r => (r.Status == RentalOperationReceipt.StatusPending || r.Status == RentalOperationReceipt.StatusProcessing) &&
                        r.RetryCount < 10 &&
                        (r.NextRetryAt == null || r.NextRetryAt <= utcNow) &&
                        !excluded.Contains(r.Action))
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .Select(r => new OperationReceiptDescriptor(
                r.Id,
                r.RentalOrderId,
                r.OperationId,
                r.Action,
                r.TargetItemId,
                r.Status,
                r.RetryCount,
                r.NextRetryAt,
                r.CreatedAt,
                r.PrerequisiteOperationId,
                r.IsOutcomeUncertain))
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
