using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Integration;

public interface IPaymentEventsDispatcher
{
    Task<Result> ReceiveMessageAsync(
        string messageJson,
        string? topic = null,
        int? partition = null,
        long? offset = null,
        CancellationToken cancellationToken = default);

    Task<Result> ProcessInboxMessageAsync(
        Guid inboxMessageId,
        CancellationToken cancellationToken = default);

    Task<Result> DispatchRawMessageAsync(string messageJson, CancellationToken cancellationToken = default);
}
