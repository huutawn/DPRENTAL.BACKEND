using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services.Integrity;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoiceLifecycleService : IInvoiceLifecycleService
{
    private readonly IInvoiceRepository _repository;
    private readonly ITransactionJournalRepository? _journalRepository;
    private readonly IPaymentDataEncryptor _encryptor;
    private readonly TimeProvider _timeProvider;

    public InvoiceLifecycleService(
        IInvoiceRepository repository,
        ITransactionJournalRepository? journalRepository,
        IPaymentDataEncryptor encryptor,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _journalRepository = journalRepository;
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }


    public async Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(
        string? routeInvoiceRef,
        CancelInvoiceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(routeInvoiceRef?.Trim(), request.InvoiceRef?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvoiceRefMismatch", "InvoiceRef trong route và body không khớp.");
        }

        var validationResult = request.Validate();
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string fingerprint = PayloadFingerprinter.ComputeCancelFingerprint(
            request.RentalOrderId,
            request.InvoiceRef!,
            request.Reason!);

        // 1. Check idempotency receipt first
        var existingReceipt = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
        if (existingReceipt is not null)
        {
            if (OperationReceiptReplayHelper.TryReplay<CancelInvoiceResponse>(
                existingReceipt,
                "CancelInvoice",
                fingerprint,
                parsed => new CancelInvoiceResponse(
                    parsed.InvoiceRef,
                    parsed.Cancelled,
                    parsed.CancelledAt.ToUniversalTime(),
                    IsReplay: true),
                out var replayResult))
            {
                return replayResult;
            }

            return replayResult;
        }

        // 2. Execute cancellation inside row-locked transaction
        await using var tx = await _repository.BeginTransactionAsync(ct);
        try
        {
            var invoice = await _repository.FindInvoiceWithRowLockAsync(request.InvoiceRef!, ct);
            if (invoice is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn cần hủy.");
            }

            if (invoice.RentalOrderId != request.RentalOrderId)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Hóa đơn không thuộc RentalOrderId tương ứng.");
            }

            var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

            // Check if deadline already passed for pending invoice
            if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
            {
                invoice.Expire(utcNow);
            }

            CancelInvoiceResponse response;
            if (invoice.Status == InvoiceStatus.Paid)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("INVOICE_ALREADY_PAID", "Hóa đơn đã được thanh toán, không thể hủy.");
            }
            else if (invoice.Status == InvoiceStatus.Cancelled)
            {
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    (invoice.CancelledAt ?? utcNow).ToUniversalTime(),
                    IsReplay: false);
            }
            else if (invoice.Status == InvoiceStatus.Expired)
            {
                // Acknowledge closure with cancelled: true while retaining EXPIRED status
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    utcNow,
                    IsReplay: false);
            }
            else
            {
                invoice.Cancel(request.Reason, utcNow);
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    utcNow,
                    IsReplay: false);
            }

            var receipt = OperationReceipt.Create(
                request.OperationKey,
                "CancelInvoice",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            var prev = await _repository.GetLatestJournalEntryAsync(invoice.InvoiceRef, ct);
            long nextVersion = (prev?.Version ?? 0) + 1;
            string prevHash = prev?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

            var cancelJournal = JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: invoice.InvoiceRef,
                streamType: JournalStreamTypes.Invoice,
                version: nextVersion,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.InvoiceCancelled,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: invoice.Status,
                confirmationSource: JournalConfirmationSources.System,
                actor: $"RENTER:{invoice.RenterId:D}",
                timestampUtc: utcNow,
                previousHash: prevHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["invoiceRef"] = invoice.InvoiceRef,
                    ["reason"] = request.Reason ?? string.Empty
                },
                snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision),
                entityClaims: [OperationalSnapshotEngine.CreateInvoiceClaim(invoice), OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt)],
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: _encryptor);

            await _repository.SaveInvoiceChangesAsync(invoice, receipt, null, cancelJournal, ct);
            await tx.CommitAsync(ct);

            return Result.Success(response);
        }
        catch (StorageConstraintViolationException ex)
        {
            await tx.RollbackAsync(ct);

            if (ex.ViolationType == StorageConstraintViolationType.OperationKey)
            {
                var raced = await _repository.GetOperationReceiptAsync(request.OperationKey, ct);
                if (raced is not null &&
                    OperationReceiptReplayHelper.TryReplay<CancelInvoiceResponse>(
                        raced,
                        "CancelInvoice",
                        fingerprint,
                        parsed => new CancelInvoiceResponse(
                            parsed.InvoiceRef,
                            parsed.Cancelled,
                            parsed.CancelledAt.ToUniversalTime(),
                            IsReplay: true),
                        out var replayResult))
                {
                    return replayResult;
                }

                return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
            }

            throw;
        }
    }

    public async Task<Result<GetInvoiceResponse>> GetInvoiceAsync(
        string invoiceRef,
        Guid callerRenterId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef))
        {
            return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
        }

        var invoice = await _repository.GetInvoiceSnapshotAsync(invoiceRef, ct);

        if (invoice is null || invoice.RenterId != callerRenterId)
        {
            return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
        }

        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

        // Expire on read if deadline has passed
        if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
        {
            await using var tx = await _repository.BeginTransactionAsync(ct);
            var locked = await _repository.FindInvoiceWithRowLockAsync(invoiceRef, ct);
            if (locked is not null)
            {
                if (locked.Status == InvoiceStatus.Pending && utcNow >= locked.PaymentDeadline)
                {
                    locked.Expire(utcNow);
                    var prev = await _repository.GetLatestJournalEntryAsync(locked.InvoiceRef, ct);
                    long nextVersion = (prev?.Version ?? 0) + 1;
                    string prevHash = prev?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

                    var expireJournal = JournalEntryFactory.CreateEntry(
                        operationId: Guid.NewGuid(),
                        streamId: locked.InvoiceRef,
                        streamType: JournalStreamTypes.Invoice,
                        version: nextVersion,
                        eventId: Guid.NewGuid(),
                        eventType: JournalEventTypes.InvoiceExpired,
                        amount: locked.TotalAmount,
                        currency: locked.Currency,
                        status: InvoiceStatus.Expired,
                        confirmationSource: JournalConfirmationSources.System,
                        actor: "SYSTEM",
                        timestampUtc: utcNow,
                        previousHash: prevHash,
                        relatedEntities: new Dictionary<string, string?>
                        {
                            ["invoiceRef"] = locked.InvoiceRef,
                            ["paymentDeadline"] = CanonicalJournalCodec.NormalizeTimestamp(locked.PaymentDeadline)
                        },
                        snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(locked, locked.Revision),
                        entityClaims: [OperationalSnapshotEngine.CreateInvoiceClaim(locked)],
                        paymentRef: locked.PaymentRef,
                        invoiceRef: locked.InvoiceRef,
                        rentalOrderId: locked.RentalOrderId,
                        encryptor: _encryptor);

                    await _repository.SaveInvoiceChangesAsync(locked, null, null, expireJournal, ct);
                    await tx.CommitAsync(ct);
                }
                else
                {
                    await tx.RollbackAsync(ct);
                }

                invoice = locked;
            }
            else
            {
                await tx.RollbackAsync(ct);
            }
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

        var response = new GetInvoiceResponse(
            invoice.InvoiceRef,
            invoice.TotalAmount,
            invoice.Currency,
            qrUrl,
            invoice.MerchantBank,
            invoice.MerchantAccount,
            invoice.PaymentCode,
            invoice.PaymentDeadline,
            invoice.Status);

        return Result.Success(response);
    }

    public async Task<int> ExpirePendingInvoicesBatchAsync(
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

        var candidateIds = await _repository.GetPendingExpiredCandidateIdsAsync(utcNow, batchSize, ct);

        if (candidateIds.Count == 0)
        {
            return 0;
        }

        int expiredCount = 0;
        await using var tx = await _repository.BeginTransactionAsync(ct);
        try
        {
            foreach (var id in candidateIds)
            {
                var locked = await _repository.FindInvoiceByIdWithRowLockSkipLockedAsync(id, ct);
                if (locked is not null && locked.Status == InvoiceStatus.Pending && locked.PaymentDeadline <= utcNow)
                {
                    locked.Expire(utcNow);
                    expiredCount++;

                    var prev = await _repository.GetLatestJournalEntryAsync(locked.InvoiceRef, ct);
                    long nextVersion = (prev?.Version ?? 0) + 1;
                    string prevHash = prev?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

                    var expireJournal = JournalEntryFactory.CreateEntry(
                        operationId: Guid.NewGuid(),
                        streamId: locked.InvoiceRef,
                        streamType: JournalStreamTypes.Invoice,
                        version: nextVersion,
                        eventId: Guid.NewGuid(),
                        eventType: JournalEventTypes.InvoiceExpired,
                        amount: locked.TotalAmount,
                        currency: locked.Currency,
                        status: InvoiceStatus.Expired,
                        confirmationSource: JournalConfirmationSources.System,
                        actor: "SYSTEM",
                        timestampUtc: utcNow,
                        previousHash: prevHash,
                        relatedEntities: new Dictionary<string, string?>
                        {
                            ["invoiceRef"] = locked.InvoiceRef,
                            ["paymentDeadline"] = CanonicalJournalCodec.NormalizeTimestamp(locked.PaymentDeadline)
                        },
                        snapshot: new Dictionary<string, object?>
                        {
                            ["status"] = locked.Status
                        },
                        paymentRef: locked.PaymentRef,
                        invoiceRef: locked.InvoiceRef,
                        rentalOrderId: locked.RentalOrderId,
                        encryptor: _encryptor);

                    await _repository.SaveInvoiceChangesAsync(locked, null, null, expireJournal, ct);
                }
            }
            await _repository.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return expiredCount;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task EnsureExpiredIfPastDeadlineAsync(string invoiceRef, DateTimeOffset utcNow, CancellationToken ct)
    {
        var snapshot = await _repository.GetInvoiceSnapshotAsync(invoiceRef, ct);

        if (snapshot is not null && snapshot.Status == InvoiceStatus.Pending && utcNow >= snapshot.PaymentDeadline)
        {
            await using var tx = await _repository.BeginTransactionAsync(ct);
            var locked = await _repository.FindInvoiceWithRowLockAsync(invoiceRef, ct);
            if (locked is not null && locked.Status == InvoiceStatus.Pending && utcNow >= locked.PaymentDeadline)
            {
                locked.Expire(utcNow);
                var prev = await _repository.GetLatestJournalEntryAsync(locked.InvoiceRef, ct);
                long nextVersion = (prev?.Version ?? 0) + 1;
                string prevHash = prev?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

                var expireJournal = JournalEntryFactory.CreateEntry(
                    operationId: Guid.NewGuid(),
                    streamId: locked.InvoiceRef,
                    streamType: JournalStreamTypes.Invoice,
                    version: nextVersion,
                    eventId: Guid.NewGuid(),
                    eventType: JournalEventTypes.InvoiceExpired,
                    amount: locked.TotalAmount,
                    currency: locked.Currency,
                    status: InvoiceStatus.Expired,
                    confirmationSource: JournalConfirmationSources.System,
                    actor: "SYSTEM",
                    timestampUtc: utcNow,
                    previousHash: prevHash,
                    relatedEntities: new Dictionary<string, string?>
                    {
                        ["invoiceRef"] = locked.InvoiceRef,
                        ["paymentDeadline"] = CanonicalJournalCodec.NormalizeTimestamp(locked.PaymentDeadline)
                    },
                    snapshot: new Dictionary<string, object?>
                    {
                        ["status"] = locked.Status
                    },
                    paymentRef: locked.PaymentRef,
                    invoiceRef: locked.InvoiceRef,
                    rentalOrderId: locked.RentalOrderId,
                    encryptor: _encryptor);

                await _repository.SaveInvoiceChangesAsync(locked, null, null, expireJournal, ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                await tx.RollbackAsync(ct);
            }
        }
    }
}
