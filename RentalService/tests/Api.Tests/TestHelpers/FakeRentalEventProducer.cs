using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public sealed class FakeRentalEventProducer : IRentalEventProducer
{
    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCreatedAsync(
        RentalOrderCreatedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderCreated", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueCartItemAddedAsync(
        CartItemAddedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "CartItemAdded", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemPaidAsync(
        RentalItemPaidPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemPaid", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCompletedAsync(
        RentalItemCompletedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemCompleted", "topic", "key")));
}
