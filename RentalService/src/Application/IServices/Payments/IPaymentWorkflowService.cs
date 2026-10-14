using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Payments;

public interface IPaymentWorkflowService
{
    Task<Result> ApplyInvoicePaidStagedAsync(
        Dprental.RentalService.Domain.Entities.RentalOrder order,
        KafkaMessageEnvelope<InvoicePaidPayload> envelope,
        Dprental.RentalService.Domain.Entities.RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default);

    Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default);
}
