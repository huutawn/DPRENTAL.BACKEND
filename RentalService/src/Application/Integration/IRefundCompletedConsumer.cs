using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Integration;

public interface IRefundCompletedConsumer
{
    Task<Result> ApplyRefundCompletedStagedAsync(
        Dprental.RentalService.Domain.Entities.RentalOrder order,
        PaymentEventEnvelope<RefundCompletedPayload> envelope,
        Dprental.RentalService.Domain.Entities.RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default);

    Task<Result> ProcessRefundCompletedAsync(PaymentEventEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default);
}
