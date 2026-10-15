using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Business producer for staging Rental domain events into the durable Outbox.
/// </summary>
public interface IRentalEventProducer
{
    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCreatedAsync(
        RentalOrderCreatedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
}
