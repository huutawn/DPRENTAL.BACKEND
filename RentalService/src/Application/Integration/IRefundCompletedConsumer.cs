using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Integration;

public interface IRefundCompletedConsumer
{
    Task<Result> ProcessRefundCompletedAsync(PaymentEventEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default);
}
