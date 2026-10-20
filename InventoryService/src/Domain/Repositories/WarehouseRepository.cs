using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai IWarehouseRepository sử dụng Entity Framework Core.
/// </summary>
public sealed class WarehouseRepository : IWarehouseRepository
{
    private readonly InventoryServiceDbContext _context;

    public WarehouseRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<Warehouse?> GetByOwnerUserIdAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            return null;
        }

        return await _context.Warehouses
            .FirstOrDefaultAsync(w => w.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty)
        {
            return false;
        }

        return await _context.Warehouses
            .AnyAsync(w => w.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(Warehouse warehouse)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        _context.Warehouses.Add(warehouse);
    }
}
