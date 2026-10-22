using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services.Integrity;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Service log messages.")]
public sealed class InvoiceCreationService : IInvoiceCreationService
{
    private readonly IInvoiceRepository _repository;
    private readonly IOptions<PaymentMerchantOptions> _merchantOptions;
    private readonly IInvoiceLifecycleService _lifecycleService;
    private readonly IPaymentDataEncryptor _encryptor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvoiceCreationService>? _logger;
    private readonly Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? _notifier;

    public InvoiceCreationService(
        IInvoiceRepository repository,
        IOptions<PaymentMerchantOptions> merchantOptions,
        IInvoiceLifecycleService lifecycleService,
        IPaymentDataEncryptor encryptor,
        TimeProvider? timeProvider = null,
        ILogger<InvoiceCreationService>? logger = null,
        Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? notifier = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _notifier = notifier;
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
                ReplayFingerprint(existingReceipt, request, fingerprint),
                parsed => parsed,
                out var replayResult))
            {
                return await BuildReplayResponseAsync(replayResult.Value.InvoiceRef, utcNow, ct);
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
                    ReplayFingerprint(racedReceipt, request, fingerprint),
                    parsed => parsed,
                    out var replayResult))
                {
                    return await BuildReplayResponseAsync(replayResult.Value.InvoiceRef, utcNow, ct);
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

            string? qrUrl = null;
            if (invoice.Status == InvoiceStatus.Pending && invoice.TotalAmount > 0m)
            {
                qrUrl = VietQrUrlGenerator.Generate(
                    invoice.MerchantBank,
                    invoice.MerchantAccount,
                    invoice.TotalAmount,
                    invoice.PaymentCode);
            }

            var response = new CreateInvoiceResponse(
                invoice.InvoiceRef,
                invoice.TotalAmount,
                invoice.Currency,
                invoice.PaymentDeadline,
                invoice.Status.ToString(),
                qrUrl,
                invoice.MerchantBank,
                invoice.MerchantAccount,
                invoice.PaymentCode,
                IsReplay: false);

            var receipt = OperationReceipt.Create(
                request.OperationKey,
                "CreateInvoice",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            Guid operationId = Guid.NewGuid();
            Guid eventId = Guid.NewGuid();
            if (outbox is not null)
            {
                outbox.LinkAttestation(eventId, operationId);
            }

            var journalEntry = JournalEntryFactory.CreateEntry(
                operationId: operationId,
                streamId: invoice.InvoiceRef,
                streamType: JournalStreamTypes.Invoice,
                version: 1,
                eventId: eventId,
                eventType: JournalEventTypes.InvoiceCreated,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: invoice.Status,
                confirmationSource: JournalConfirmationSources.System,
                actor: $"RENTER:{invoice.RenterId:D}",
                timestampUtc: utcNow,
                previousHash: CanonicalJournalHasher.GenesisHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["invoiceRef"] = invoice.InvoiceRef,
                    ["orderCode"] = invoice.OrderCode,
                    ["rentalOrderId"] = invoice.RentalOrderId.ToString("D"),
                    ["paymentCode"] = invoice.PaymentCode,
                    ["paymentDeadline"] = CanonicalJournalCodec.NormalizeTimestamp(invoice.PaymentDeadline)
                },
                snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision),
                entityClaims: new List<CanonicalEntityClaim>
                {
                    OperationalSnapshotEngine.CreateInvoiceClaim(invoice),
                    OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt)
                }.Concat(invoice.Items.Select(OperationalSnapshotEngine.CreateInvoiceItemClaim))
                 .Concat(outbox != null ? [OperationalSnapshotEngine.CreateOutboxClaim(outbox)] : Enumerable.Empty<CanonicalEntityClaim>())
                 .ToList(),
                outboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(outbox?.Payload),
                outboxMessageId: outbox?.MessageId,
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: _encryptor);

            await using var tx = await _repository.BeginTransactionAsync(ct);
            try
            {
                await _repository.SaveNewInvoiceAsync(invoice, receipt, outbox, journalEntry, ct);
                await tx.CommitAsync(ct);
                if (_notifier is not null && invoice.TotalAmount == 0m)
                {
                    await _notifier.NotifyPaymentCompletedAsync(invoice.RenterId, eventId, invoice, ct);
                }

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
                            ReplayFingerprint(raced, request, fingerprint),
                            parsed => parsed,
                            out var replayResult))
                        {
                            return await BuildReplayResponseAsync(replayResult.Value.InvoiceRef, utcNow, ct);
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

    private static string ReplayFingerprint(OperationReceipt receipt, CreateInvoiceRequest request, string fingerprint)
    {
        // PostgreSQL truncates 100ns ticks to microseconds. Reuse the original deadline only
        // when the request identifies the same persisted instant; all other fields remain hashed.
        try
        {
            var original = JsonSerializer.Deserialize<CreateInvoiceResponse>(receipt.ResultJson);
            if (original is not null &&
                original.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond ==
                request.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond)
            {
                return PayloadFingerprinter.ComputeCreateFingerprint(
                    request.RentalOrderId, request.OrderCode, request.RenterId, original.PaymentDeadline,
                    request.Items?.Where(i => i is not null)
                        .Select(i => (i.RentalItemId, i.SupplierId, i.RentalAmount, i.DepositAmount)));
            }
        }
        catch (JsonException)
        {
            // The replay helper reports an invalid stored receipt through its existing error path.
        }

        return fingerprint;
    }

    private async Task<Result<CreateInvoiceResponse>> BuildReplayResponseAsync(
        string invoiceRef,
        DateTimeOffset utcNow,
        CancellationToken ct)
    {
        await _lifecycleService.EnsureExpiredIfPastDeadlineAsync(invoiceRef, utcNow, ct);
        var currentInvoice = await _repository.GetInvoiceSnapshotAsync(invoiceRef, ct);
        if (currentInvoice is null)
        {
            return Error.NotFound("INVOICE_NOT_FOUND", "Hóa đơn không tồn tại.");
        }

        string currentStatus = currentInvoice.Status.ToString();
        string? currentQrUrl = null;
        if (currentInvoice.Status == InvoiceStatus.Pending && currentInvoice.TotalAmount > 0m && utcNow < currentInvoice.PaymentDeadline)
        {
            currentQrUrl = VietQrUrlGenerator.Generate(
                currentInvoice.MerchantBank,
                currentInvoice.MerchantAccount,
                currentInvoice.TotalAmount,
                currentInvoice.PaymentCode);
        }

        return Result.Success(new CreateInvoiceResponse(
            currentInvoice.InvoiceRef,
            currentInvoice.TotalAmount,
            currentInvoice.Currency,
            currentInvoice.PaymentDeadline.ToUniversalTime(),
            currentStatus,
            currentQrUrl,
            currentInvoice.MerchantBank,
            currentInvoice.MerchantAccount,
            currentInvoice.PaymentCode,
            IsReplay: true));
    }
}
