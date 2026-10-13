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
    private readonly IPaymentRecordingService _recordingService;

    public InvoiceService(
        IInvoiceCreationService creationService,
        IInvoiceLifecycleService lifecycleService,
        IPaymentRecordingService recordingService)
    {
        _creationService = creationService ?? throw new ArgumentNullException(nameof(creationService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _recordingService = recordingService ?? throw new ArgumentNullException(nameof(recordingService));
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

    public Task<Result> RecordPaymentReceivedAsync(
        string invoiceRef,
        string paymentRef,
        decimal amount,
        string currency,
        DateTimeOffset paidAt,
        CancellationToken ct = default) =>
        _recordingService.RecordPaymentReceivedAsync(invoiceRef, paymentRef, amount, currency, paidAt, ct);

    public Task<int> ExpirePendingInvoicesBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default) =>
        _lifecycleService.ExpirePendingInvoicesBatchAsync(batchSize, ct);
}
