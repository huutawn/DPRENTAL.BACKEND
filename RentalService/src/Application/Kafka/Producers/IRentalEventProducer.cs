using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Ghi sự kiện nghiệp vụ Rental vào Outbox trong transaction của use case.
/// Event thông báo kết quả đã ghi nhận; khác command yêu cầu service khác thực hiện một việc.
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

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderExpiredAsync(
        RentalOrderExpiredPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderRejectedAsync(
        RentalOrderRejectedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCancelledAsync(
        RentalOrderCancelledPayload payload,
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

    Task<Result<ProducerEnqueueResult>> EnqueueRentalUnitsReturnedAsync(
        Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalSupplierConfirmationRequestedAsync(
        RentalSupplierConfirmationRequestedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemConfirmedAsync(
        RentalItemConfirmedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemRejectedAsync(
        RentalItemRejectedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemExpiredAsync(
        RentalItemExpiredPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCancelledAsync(
        RentalItemCancelledPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalItemHandoverCompletedAsync(
        RentalItemHandoverCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueRentalHandoverOverdueAsync(
        RentalHandoverOverduePayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    Task<Result<ProducerEnqueueResult>> EnqueueReturnConfirmationOverdueAsync(
        ReturnConfirmationOverduePayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
    Task<Result<ProducerEnqueueResult>> EnqueueLatePaymentRefundRequestedAsync(
        LatePaymentRefundRequestedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);
}
