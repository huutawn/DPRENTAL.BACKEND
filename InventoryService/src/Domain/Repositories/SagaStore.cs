using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store quản lý phiên saga phân tán bằng EF Core.
/// </summary>
public sealed class SagaStore : ISagaStore
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo store với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public SagaStore(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<SagaInstance?> GetAsync(
        string sagaType,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.SagaInstances
            .FirstOrDefaultAsync(s => s.SagaType == sagaType && s.CorrelationId == correlationId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(SagaInstance saga)
    {
        _context.SagaInstances.Add(saga);
    }

    /// <inheritdoc />
    public void Update(SagaInstance saga)
    {
        _context.SagaInstances.Update(saga);
    }
}
