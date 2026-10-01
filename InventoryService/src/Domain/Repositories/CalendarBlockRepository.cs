using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository quản lý các khoảng thời gian chặn lịch (CalendarBlock) bằng EF Core.
/// </summary>
public sealed class CalendarBlockRepository : ICalendarBlockRepository
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo repository với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public CalendarBlockRepository(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<CalendarBlock?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarBlock>> GetOverlappingBlocksAsync(
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .Where(b => b.EquipmentId == equipmentId && b.StartDate <= endDate && b.EndDate >= startDate)
            .OrderBy(b => b.StartDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarBlock>> ListByEquipmentAsync(
        Guid equipmentId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.EquipmentId == equipmentId)
            .OrderByDescending(b => b.StartDate)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(CalendarBlock block)
    {
        _context.CalendarBlocks.Add(block);
    }

    /// <inheritdoc />
    public void Remove(CalendarBlock block)
    {
        _context.CalendarBlocks.Remove(block);
    }
}
