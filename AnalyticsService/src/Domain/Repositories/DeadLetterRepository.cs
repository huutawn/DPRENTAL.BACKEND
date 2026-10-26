using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

/// <summary>
/// Cài đặt <see cref="IDeadLetterRepository"/> bằng EF Core. Không gọi SaveChanges.
/// </summary>
public sealed class DeadLetterRepository : IDeadLetterRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public DeadLetterRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // Dùng index duy nhất (Topic, Partition, Offset)
    public Task<bool> ExistsAsync(string topic, int partition, long offset, CancellationToken cancellationToken = default) =>
        _context.AnalyticsDeadLetters.AnyAsync(
            deadLetter => deadLetter.Topic == topic && deadLetter.Partition == partition && deadLetter.Offset == offset,
            cancellationToken);

    public void Add(AnalyticsDeadLetter deadLetter) => _context.AnalyticsDeadLetters.Add(deadLetter);
}
