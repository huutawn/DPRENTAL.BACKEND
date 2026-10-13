using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.Payments;

public interface IPaymentWorkflowService
{
    Task<Result> ApplyInvoicePaidStagedAsync(
        Dprental.RentalService.Domain.Entities.RentalOrder order,
        PaymentEventEnvelope<InvoicePaidPayload> envelope,
        Dprental.RentalService.Domain.Entities.RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default);

    Task<Result> ProcessInvoicePaidAsync(PaymentEventEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default);
}
