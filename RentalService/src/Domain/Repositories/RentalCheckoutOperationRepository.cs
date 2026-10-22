using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RentalCheckoutOperationRepository : IRentalCheckoutOperationRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public RentalCheckoutOperationRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }
    public async Task<RentalCheckoutOperation?> GetByExpectedOrderIdAsync(Guid expectedOrderId, CancellationToken cancellationToken = default)
    {
        if (expectedOrderId == Guid.Empty) return null;
        return await _dbContext.RentalCheckoutOperations
            .FirstOrDefaultAsync(o => o.ExpectedRentalOrderId == expectedOrderId, cancellationToken);
    }


    public async Task<RentalCheckoutOperation?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;
        return await _dbContext.RentalCheckoutOperations
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Trim(), cancellationToken);
    }

    public async Task<RentalCheckoutOperation?> GetByOperationIdForRenterAsync(string operationId, Guid renterUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;
        return await _dbContext.RentalCheckoutOperations
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Trim() && o.RenterUserId == renterUserId, cancellationToken);
    }

    public async Task<RentalCheckoutOperation?> GetActiveOperationByRenterAsync(Guid renterUserId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalCheckoutOperations
            .FirstOrDefaultAsync(o => o.RenterUserId == renterUserId && o.Status == CheckoutOperationStatus.Processing, cancellationToken);
    }

    public async Task<IReadOnlyList<RentalCheckoutOperation>> GetTimedOutOperationsAsync(DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalCheckoutOperations
            .Where(o => o.Status == CheckoutOperationStatus.Processing && o.HoldExpiresAt <= now)
            .OrderBy(o => o.HoldExpiresAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalCheckoutOperation>> GetPendingProcessingOperationsAsync(DateTimeOffset now, int limit = 50, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalCheckoutOperations
            .Where(o => o.Status == CheckoutOperationStatus.Processing && o.HoldExpiresAt > now)
            .OrderBy(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RentalCheckoutOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _dbContext.RentalCheckoutOperations.AddAsync(operation, cancellationToken);
    }

    public Task UpdateAsync(RentalCheckoutOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _dbContext.RentalCheckoutOperations.Update(operation);
        return Task.CompletedTask;
    }
}
