using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Business producer for staging Rental domain events into the durable Outbox.
/// </summary>
public interface IRentalEventProducer
{
    Task<Result<ProducerEnqueueResult>> EnqueueDomainEventAsync<TPayload>(
        string messageType,
        string logicalEntityId,
        string aggregateId,
        DateTimeOffset occurredAt,
        TPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCreatedAsync(
        RentalOrderCreatedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderPaidAsync(
        RentalOrderPaidPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCompletedAsync(
        RentalOrderCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueCartItemAddedAsync(
        CartItemAddedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemPaidAsync(
        RentalItemPaidPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCompletedAsync(
        RentalItemCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemReturnedAsync(
        RentalItemReturnedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
}
