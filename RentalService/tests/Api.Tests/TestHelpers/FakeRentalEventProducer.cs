using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public sealed class FakeRentalEventProducer : IRentalEventProducer
{
    public List<RentalItemReturnedPayload> ReturnedEvents { get; } = [];
    public Task<Result<ProducerEnqueueResult>> EnqueueDomainEventAsync<TPayload>(string messageType,
        string logicalEntityId, string aggregateId, DateTimeOffset occurredAt, TPayload payload,
        string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, messageType, "topic", aggregateId)));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCreatedAsync(
        RentalOrderCreatedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderCreated", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderPaidAsync(
        RentalOrderPaidPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderPaid", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCompletedAsync(
        RentalOrderCompletedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderCompleted", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueCartItemAddedAsync(
        CartItemAddedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "CartItemAdded", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemPaidAsync(
        RentalItemPaidPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemPaid", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCompletedAsync(
        RentalItemCompletedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemCompleted", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemReturnedAsync(
        RentalItemReturnedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default)
    {
        ReturnedEvents.Add(payload);
        return Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemReturned", "topic", "key")));
    }

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderExpiredAsync(
        RentalOrderExpiredPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderExpired", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCancelledAsync(
        RentalOrderCancelledPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalOrderCancelled", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalUnitsReturnedAsync(
        Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalUnitsReturned", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalSupplierConfirmationRequestedAsync(
        RentalSupplierConfirmationRequestedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalSupplierConfirmationRequested", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemConfirmedAsync(
        RentalItemConfirmedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemConfirmed", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemRejectedAsync(
        RentalItemRejectedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemRejected", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemExpiredAsync(
        RentalItemExpiredPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemExpired", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCancelledAsync(
        RentalItemCancelledPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemCancelled", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalItemHandoverCompletedAsync(
        RentalItemHandoverCompletedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalItemHandoverCompleted", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueRentalHandoverOverdueAsync(
        RentalHandoverOverduePayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "RentalHandoverOverdue", "topic", "key")));

    public Task<Result<ProducerEnqueueResult>> EnqueueLatePaymentRefundRequestedAsync(
        LatePaymentRefundRequestedPayload payload, string? correlationId = null, Guid? causationId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "LatePaymentRefundRequested", "topic", "key")));
}
