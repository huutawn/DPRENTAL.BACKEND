using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Sổ ghi các message Kafka đã xử lý, dùng để bỏ qua message bị gửi lại.
/// </summary>
public interface IInboxRepository
{
    Task<bool> ExistsAsync(Guid messageId, CancellationToken cancellationToken = default);

    void Add(AnalyticsInboxMessage message);
}
