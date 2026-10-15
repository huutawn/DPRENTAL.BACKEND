using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IInvoicePaymentProcessor
{
    Task<Result> ProcessAsync(SePayWebhookPayload? payload, CancellationToken cancellationToken = default);
}
