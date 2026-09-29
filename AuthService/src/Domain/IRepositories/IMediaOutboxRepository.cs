using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

/// <summary>Repository quản lý hàng đợi outbox cho các tác vụ đồng bộ trạng thái Media.</summary>
public interface IMediaOutboxRepository
{
    Task AddAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaOutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default);
    Task UpdateAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default);
}
