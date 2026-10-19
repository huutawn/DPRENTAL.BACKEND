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

    public async Task<IReadOnlyList<OperationReceiptDescriptor>> GetPendingOperationDescriptorsAsync(
        DateTimeOffset utcNow,
        int batchSize,
        IReadOnlyCollection<string>? excludedActions = null,
        IReadOnlyCollection<string>? unlimitedAttemptActions = null,
        CancellationToken cancellationToken = default)
    {
        string[] excluded = excludedActions?.ToArray() ?? [];
        string[] unlimited = unlimitedAttemptActions?.ToArray() ?? [];

        return await _dbContext.RentalOperationReceipts
            .AsNoTracking()
            .Where(r => (excluded.Length == 0 || !excluded.Contains(r.Action)) &&
                (
                    ((r.Status == RentalOperationReceipt.StatusPending || r.Status == RentalOperationReceipt.StatusProcessing) &&
                     (r.NextRetryAt == null || r.NextRetryAt <= utcNow) &&
                     (unlimited.Contains(r.Action) || r.RetryCount < 10))
                    ||
                    (unlimited.Contains(r.Action) &&
                     r.Status == RentalOperationReceipt.StatusFailed &&
                     (r.ErrorClassification == RentalOperationReceipt.ClassificationTransientExhausted ||
                      r.ErrorClassification == RentalOperationReceipt.ClassificationUncertainExhausted) &&
                     (r.NextRetryAt == null || r.NextRetryAt <= utcNow))
                ))
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
