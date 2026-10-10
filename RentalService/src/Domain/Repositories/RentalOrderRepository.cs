using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

/// <summary>
/// EF Core implementation of IRentalOrderRepository.
/// </summary>
public sealed class RentalOrderRepository : IRentalOrderRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public RentalOrderRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.OrderCode == orderCode.Trim(), cancellationToken);
    }

    public async Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.CheckoutOperationId == operationId.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.RenterId == renterId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.Items.Any(i => i.SupplierId == supplierId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await _dbContext.RentalOrders.AddAsync(order, cancellationToken);
        return order;
    }

    public Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        _dbContext.RentalOrders.Update(order);
        return Task.FromResult(true);
    }
}
