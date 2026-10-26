using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Cài đặt <see cref="IInboxRepository"/> bằng EF Core. Không gọi SaveChanges.
/// </summary>
public sealed class InboxRepository : IInboxRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public InboxRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // AnyAsync chạy trên DB (dùng khóa chính MessageId), chỉ trả true/false, không tải dòng về
    public Task<bool> ExistsAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _context.AnalyticsInboxMessages.AnyAsync(message => message.MessageId == messageId, cancellationToken);

    public void Add(AnalyticsInboxMessage message) => _context.AnalyticsInboxMessages.Add(message);
}
