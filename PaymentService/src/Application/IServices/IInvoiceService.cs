using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IInvoiceService
{
    Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default);

    Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        string? routeInvoiceRef,
        CancelInvoiceRequest request,
        CancellationToken ct = default);

    Task<Result<GetInvoiceResponse>> GetInvoiceAsync(
        string invoiceRef,
        Guid callerRenterId,
        CancellationToken ct = default);
    Task<int> ExpirePendingInvoicesBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default);
}
