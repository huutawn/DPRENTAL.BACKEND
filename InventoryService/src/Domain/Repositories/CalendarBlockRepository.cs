using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và quản lý các khoảng chặn lịch (CalendarBlock) trên PostgreSQL.
/// </summary>
public sealed class CalendarBlockRepository : ICalendarBlockRepository
{
    private readonly InventoryServiceDbContext _context;

    public CalendarBlockRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<CalendarBlock?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .FirstOrDefaultAsync(cb => cb.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarBlock>> GetOverlappingBlocksAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .Where(cb => cb.AssetId == assetId && cb.StartDate <= endDate && cb.EndDate >= startDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarBlock>> ListByAssetAsync(
        Guid assetId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.CalendarBlocks
            .AsNoTracking()
            .Where(cb => cb.AssetId == assetId)
            .OrderByDescending(cb => cb.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(CalendarBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _context.CalendarBlocks.Add(block);
    }

    /// <inheritdoc />
    public void Remove(CalendarBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _context.CalendarBlocks.Remove(block);
    }
}
