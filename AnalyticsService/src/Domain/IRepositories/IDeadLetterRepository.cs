using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>Sổ ghi vị trí message lỗi dữ liệu. Mỗi vị trí chỉ ghi một lần.</summary>
public interface IDeadLetterRepository
{
    Task<bool> ExistsAsync(string topic, int partition, long offset, CancellationToken cancellationToken = default);

    void Add(AnalyticsDeadLetter deadLetter);
}
