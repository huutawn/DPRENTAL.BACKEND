using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai IInventoryWriteContext cung cấp các repository dùng chung một scoped DbContext trong giao dịch.
/// </summary>
public sealed class InventoryWriteContext : IInventoryWriteContext
{
    private readonly InventoryServiceDbContext _context;

    public InventoryWriteContext(
        InventoryServiceDbContext context,
        Guid assetId,
        IInventoryAssetRepository assets,
        IAssetAllocationRepository allocations,
        ICalendarBlockRepository calendarBlocks)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        AssetId = assetId;
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        CalendarBlocks = calendarBlocks ?? throw new ArgumentNullException(nameof(calendarBlocks));
    }

    /// <inheritdoc />
    public Guid AssetId { get; }

    /// <inheritdoc />
    public IInventoryAssetRepository Assets { get; }

    /// <inheritdoc />
    public IAssetAllocationRepository Allocations { get; }

    /// <inheritdoc />
    public ICalendarBlockRepository CalendarBlocks { get; }

    /// <inheritdoc />
    public void StageOutbox(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.OutboxMessages.Add(message);
    }
}
