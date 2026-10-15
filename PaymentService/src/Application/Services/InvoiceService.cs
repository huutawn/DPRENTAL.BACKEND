using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoiceService : IInvoiceService
{
    private readonly IInvoiceCreationService _creationService;
    private readonly IInvoiceLifecycleService _lifecycleService;

    public InvoiceService(
        IInvoiceCreationService creationService,
        IInvoiceLifecycleService lifecycleService)
    {
        _creationService = creationService ?? throw new ArgumentNullException(nameof(creationService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
    }


    public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default) =>
        _creationService.CreateInvoiceAsync(request, ct);

    public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        string? routeInvoiceRef,
        CancelInvoiceRequest request,
        CancellationToken ct = default) =>
        _lifecycleService.CancelInvoiceAsync(routeInvoiceRef, request, ct);

    public Task<Result<GetInvoiceResponse>> GetInvoiceAsync(
        string invoiceRef,
        Guid callerRenterId,
        CancellationToken ct = default) =>
        _lifecycleService.GetInvoiceAsync(invoiceRef, callerRenterId, ct);

    public Task<int> ExpirePendingInvoicesBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default) =>
        _lifecycleService.ExpirePendingInvoicesBatchAsync(batchSize, ct);
}
