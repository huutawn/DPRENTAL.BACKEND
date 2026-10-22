using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Hub;

public interface IPaymentHubNotifier
{
    Task<Result> SendToUserAsync<T>(
        Guid userId, string eventName, T payload, CancellationToken cancellationToken = default);

    Task<Result> SendToUsersAsync<T>(
        IReadOnlyCollection<Guid> userIds, string eventName, T payload, CancellationToken cancellationToken = default);

    Task<Result> SendToGroupAsync<T>(
        string groupName, string eventName, T payload, CancellationToken cancellationToken = default);

    Task<Result> SetGroupUsersAsync(
        string groupName, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    Task<Result> RemoveGroupAsync(
        string groupName, CancellationToken cancellationToken = default);

    Task<Result> NotifyPaymentCompletedAsync(
        Guid userId, Guid eventId, Invoice invoice, CancellationToken cancellationToken = default);
}
