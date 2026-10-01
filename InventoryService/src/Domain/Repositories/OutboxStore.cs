using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store lưu trữ và truy vấn Outbox messages bằng EF Core.
/// </summary>
public sealed class OutboxStore : IOutboxStore
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo store với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public OutboxStore(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public void Add(OutboxMessage message)
    {
        _context.OutboxMessages.Add(message);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await _context.OutboxMessages
            .Where(m => m.ProcessedAt == null && (m.LockLeaseExpiresAt == null || m.LockLeaseExpiresAt.Value <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Update(OutboxMessage message)
    {
        _context.OutboxMessages.Update(message);
    }
}
