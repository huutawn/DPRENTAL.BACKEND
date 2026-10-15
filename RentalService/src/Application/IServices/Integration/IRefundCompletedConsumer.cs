using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Integration;

public interface IRefundCompletedConsumer
{
    Task<Result> ApplyRefundCompletedStagedAsync(
        Dprental.RentalService.Domain.Entities.RentalOrder order,
        KafkaMessageEnvelope<RefundCompletedPayload> envelope,
        Dprental.RentalService.Domain.Entities.RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default);

    Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default);
}
