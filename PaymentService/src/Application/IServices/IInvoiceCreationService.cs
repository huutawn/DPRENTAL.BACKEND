using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IInvoiceCreationService
{
    Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default);
}
