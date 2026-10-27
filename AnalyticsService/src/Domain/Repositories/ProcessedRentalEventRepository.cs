using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class ProcessedRentalEventRepository : IProcessedRentalEventRepository
{
    private readonly AnalyticsServiceDbContext _context;

    public ProcessedRentalEventRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<bool> ExistsAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _context.ProcessedRentalEvents.AnyAsync(x => x.MessageId == messageId, cancellationToken);

    public void Add(ProcessedRentalEvent processed) => _context.ProcessedRentalEvents.Add(processed);
}
