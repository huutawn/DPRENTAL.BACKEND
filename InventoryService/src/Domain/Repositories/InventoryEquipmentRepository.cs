using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository quản lý thông tin thiết bị trong kho Inventory sử dụng EF Core DbContext.
/// </summary>
public sealed class InventoryEquipmentRepository : IInventoryEquipmentRepository
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo repository với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public InventoryEquipmentRepository(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<InventoryEquipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Equipments
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InventoryEquipment?> GetByIdForOrganizationAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _context.Equipments
            .FirstOrDefaultAsync(e => e.Id == id && e.OrganizationId == organizationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InventoryEquipment>> ListByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Equipments
            .AsNoTracking()
            .Where(e => e.OrganizationId == organizationId)
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(InventoryEquipment equipment)
    {
        _context.Equipments.Add(equipment);
    }

    /// <inheritdoc />
    public void Update(InventoryEquipment equipment)
    {
        var entry = _context.Entry(equipment);
        if (entry.State == EntityState.Detached)
        {
            _context.Equipments.Update(equipment);
        }
    }
}
