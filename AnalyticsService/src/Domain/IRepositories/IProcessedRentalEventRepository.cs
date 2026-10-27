using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>Inbox của rental.events.v1.</summary>
public interface IProcessedRentalEventRepository
{
    Task<bool> ExistsAsync(Guid messageId, CancellationToken cancellationToken = default);

    void Add(ProcessedRentalEvent processed);
}
