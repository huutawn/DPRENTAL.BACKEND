using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IInboxReceiptRepository
{
    Task<bool> ExistsAsync(string consumerName, Guid messageId, CancellationToken cancellationToken = default);
    Task AddAsync(NotificationInboxReceipt receipt, CancellationToken cancellationToken = default);
}
