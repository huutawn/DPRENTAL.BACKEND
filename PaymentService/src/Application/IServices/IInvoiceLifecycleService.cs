using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IInvoiceLifecycleService
{
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

    Task EnsureExpiredIfPastDeadlineAsync(
        string invoiceRef,
        DateTimeOffset utcNow,
        CancellationToken ct = default);
}
