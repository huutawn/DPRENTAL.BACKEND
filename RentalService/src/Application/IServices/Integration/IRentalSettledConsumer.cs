using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.IServices.Integration;

public interface IRentalSettledConsumer
{
    Task<Result<bool>> ApplyRentalSettledStagedAsync(
        RentalOrder order, KafkaMessageEnvelope<RentalSettledPayload> envelope,
        RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default);
}
