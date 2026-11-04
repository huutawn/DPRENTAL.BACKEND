using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và deduplication bản tin Kafka nhận qua Transactional Inbox.
/// </summary>
public interface IInboxRepository
{
    /// <summary>
    /// Kiểm tra bản tin đã từng được xử lý bởi consumer group hay chưa.
    /// </summary>
    Task<bool> ExistsAsync(Guid messageId, string consumerGroup, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu bản ghi nhận bản tin vào bảng inbox_messages.
    /// </summary>
    Task AddAsync(InboxMessage inboxMessage, CancellationToken cancellationToken = default);
}
