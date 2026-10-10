using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Integration;

public interface IPaymentEventsDispatcher
{
    Task<Result> DispatchRawMessageAsync(string messageJson, CancellationToken cancellationToken = default);
}
