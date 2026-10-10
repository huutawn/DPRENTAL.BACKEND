using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Payments;

public interface IPaymentWorkflowService
{
    Task<Result> ProcessInvoicePaidAsync(PaymentEventEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default);
}
