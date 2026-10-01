using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai store lưu trữ và kiểm tra tính duy nhất của Inbox messages bằng EF Core.
/// </summary>
public sealed class InboxStore : IInboxStore
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo store với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public InboxStore(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<bool> HasBeenReceivedAsync(
        string consumer,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.InboxMessages
            .AnyAsync(m => m.Consumer == consumer && m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InboxMessage?> GetAsync(
        string consumer,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.InboxMessages
            .FirstOrDefaultAsync(m => m.Consumer == consumer && m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(InboxMessage message)
    {
        _context.InboxMessages.Add(message);
    }

    /// <inheritdoc />
    public void Update(InboxMessage message)
    {
        _context.InboxMessages.Update(message);
    }
}
