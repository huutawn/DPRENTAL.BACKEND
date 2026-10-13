using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Service log messages.")]
public sealed class InvoiceCreationService : IInvoiceCreationService
{
    private readonly IInvoiceRepository _repository;
    private readonly IOptions<PaymentMerchantOptions> _merchantOptions;
    private readonly IInvoiceLifecycleService _lifecycleService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvoiceCreationService>? _logger;

    public InvoiceCreationService(
        IInvoiceRepository repository,
        IOptions<PaymentMerchantOptions> merchantOptions,
        IInvoiceLifecycleService lifecycleService,
        TimeProvider? timeProvider = null,
        ILogger<InvoiceCreationService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = request.Validate();
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var itemsTuples = request.Items?
            .Where(i => i is not null)
            .Select(i => (i.RentalItemId, i.SupplierId, i.RentalAmount, i.DepositAmount));

        string fingerprint = PayloadFingerprinter.ComputeCreateFingerprint(
            request.RentalOrderId,
            request.OrderCode,
            request.RenterId,
            request.PaymentDeadline,
            itemsTuples);

        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var utcDeadline = request.PaymentDeadline.ToUniversalTime();

        // 1. Check idempotency receipt first
        var existingReceipt = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
        if (existingReceipt is not null)
        {
            if (OperationReceiptReplayHelper.TryReplay<CreateInvoiceResponse>(
                existingReceipt,
                "CreateInvoice",
                fingerprint,
                parsed => new CreateInvoiceResponse(
                    parsed.InvoiceRef,
                    parsed.TotalAmount,
                    parsed.Currency,
                    parsed.PaymentDeadline.ToUniversalTime(),
                    IsReplay: true),
                out var replayResult))
            {
                await _lifecycleService.EnsureExpiredIfPastDeadlineAsync(replayResult.Value.InvoiceRef, utcNow, ct);
                return replayResult;
            }

            return replayResult;
        }

        // 2. Validate payment deadline against current time
        if (utcDeadline <= utcNow)
        {
            return Error.Validation("INVALID_PAYMENT_DEADLINE", "Hạn thanh toán phải lớn hơn thời điểm tạo mới.");
        }

        // 3. Ensure one invoice per rental order
        bool orderExists = await _repository.ExistsByRentalOrderIdAsync(request.RentalOrderId, ct);
        if (orderExists)
        {
            var racedReceipt = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
            if (racedReceipt is not null)
            {
                if (OperationReceiptReplayHelper.TryReplay<CreateInvoiceResponse>(
                    racedReceipt,
                    "CreateInvoice",
                    fingerprint,
                    parsed => new CreateInvoiceResponse(
                        parsed.InvoiceRef,
                        parsed.TotalAmount,
                        parsed.Currency,
                        parsed.PaymentDeadline.ToUniversalTime(),
                        IsReplay: true),
                    out var replayResult))
                {
                    await _lifecycleService.EnsureExpiredIfPastDeadlineAsync(replayResult.Value.InvoiceRef, utcNow, ct);
                    return replayResult;
                }

                return replayResult;
            }

            return Error.Conflict("INVOICE_ALREADY_EXISTS", "Đơn hàng đã tồn tại hóa đơn.");
        }

        var merchant = _merchantOptions.Value;

        // 4. Collision retry loop for PaymentCode (up to 5 attempts)
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string paymentCode = PaymentCodeGenerator.Generate();

            var domainItems = new List<InvoiceItem>();
            foreach (var itemReq in request.Items!)
            {
                if (itemReq is null)
                {
                    continue;
                }

                var itemRes = InvoiceItem.Create(
                    itemReq.RentalItemId,
                    itemReq.SupplierId,
                    itemReq.RentalAmount,
                    itemReq.DepositAmount);

                if (itemRes.IsFailure) return itemRes.Error;
                domainItems.Add(itemRes.Value);
            }

            var invoiceRes = Invoice.Create(
                request.RentalOrderId,
                request.OrderCode,
                request.RenterId,
                request.Currency,
                utcDeadline,
                domainItems,
                paymentCode,
                merchant.Bank,
                merchant.Account,
                utcNow);

            if (invoiceRes.IsFailure) return invoiceRes.Error;
            var invoice = invoiceRes.Value;

            OutboxMessage? outbox = null;
            if (invoice.TotalAmount == 0m)
            {
                outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(invoice, utcNow);
            }

            var response = new CreateInvoiceResponse(
                invoice.InvoiceRef,
                invoice.TotalAmount,
                invoice.Currency,
                invoice.PaymentDeadline,
                IsReplay: false);

            var receipt = OperationReceipt.Create(
                request.OperationKey,
                "CreateInvoice",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            await using var tx = await _repository.BeginTransactionAsync(ct);
            try
            {
                await _repository.SaveNewInvoiceAsync(invoice, receipt, outbox, ct);
                await tx.CommitAsync(ct);

                return Result.Success(response);
            }
            catch (StorageConstraintViolationException ex)
            {
                await tx.RollbackAsync(ct);

                if (ex.ViolationType is StorageConstraintViolationType.OperationKey or StorageConstraintViolationType.RentalOrderId)
                {
                    var raced = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
                    if (raced is null)
                    {
                        await Task.Delay(50, ct);
                        raced = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
                    }

                    if (raced is not null)
                    {
                        if (OperationReceiptReplayHelper.TryReplay<CreateInvoiceResponse>(
                            raced,
                            "CreateInvoice",
                            fingerprint,
                            parsed => new CreateInvoiceResponse(
                                parsed.InvoiceRef,
                                parsed.TotalAmount,
                                parsed.Currency,
                                parsed.PaymentDeadline.ToUniversalTime(),
                                IsReplay: true),
                            out var replayResult))
                        {
                            await _lifecycleService.EnsureExpiredIfPastDeadlineAsync(replayResult.Value.InvoiceRef, utcNow, ct);
                            return replayResult;
                        }

                        return replayResult;
                    }

                    if (ex.ViolationType == StorageConstraintViolationType.RentalOrderId ||
                        await _repository.ExistsByRentalOrderIdAsync(request.RentalOrderId, ct))
                    {
                        return Error.Conflict("INVOICE_ALREADY_EXISTS", "Đơn hàng đã tồn tại hóa đơn.");
                    }
                }

                if (ex.ViolationType == StorageConstraintViolationType.PaymentCode)
                {
                    _logger?.LogWarning("PaymentCode collision on attempt {Attempt}/{MaxAttempts}; retrying...", attempt, maxAttempts);
                    continue;
                }

                throw;
            }
        }

        return Error.Failure("PAYMENT_CODE_COLLISION_EXHAUSTED", "Không thể sinh mã thanh toán duy nhất sau 5 lần thử.");
    }
}
