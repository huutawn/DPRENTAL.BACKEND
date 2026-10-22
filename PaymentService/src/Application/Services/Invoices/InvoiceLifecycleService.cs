using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services.Integrity;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Application.Policies;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoiceLifecycleService : IInvoiceLifecycleService
{
    private readonly IInvoiceRepository _repository;
    private readonly ITransactionJournalRepository? _journalRepository;
    private readonly IPaymentDataEncryptor _encryptor;
    private readonly IManualRefundRepository _refundRepository;
    private readonly ITransactionIntegrityChecker _integrityChecker;
    private readonly TimeProvider _timeProvider;
    private readonly Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? _notifier;

    public InvoiceLifecycleService(
        IInvoiceRepository repository,
        ITransactionJournalRepository? journalRepository,
        IPaymentDataEncryptor encryptor,
        IManualRefundRepository refundRepository,
        ITransactionIntegrityChecker integrityChecker,
        TimeProvider? timeProvider = null,
        Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? notifier = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _journalRepository = journalRepository;
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _refundRepository = refundRepository ?? throw new ArgumentNullException(nameof(refundRepository));
        _integrityChecker = integrityChecker ?? throw new ArgumentNullException(nameof(integrityChecker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _notifier = notifier;
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
            request.Reason!,
            request.CancellationOrigin);

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
                invoice.Cancel(request.Reason ?? string.Empty, utcNow, request.CancellationOrigin);
                response = new CancelInvoiceResponse(
                    invoice.InvoiceRef,
                    true,
                    utcNow,
                    IsReplay: false);
            }
            else
            {
                invoice.Cancel(request.Reason, utcNow, request.CancellationOrigin);
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
                snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision, invoice.IntegritySnapshotVersion),
                entityClaims: [OperationalSnapshotEngine.CreateInvoiceClaim(invoice, invoice.IntegritySnapshotVersion), OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt)],
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

    public async Task<Result<AcceptLatePaymentResponse>> AcceptLatePaymentAsync(
        string? routeInvoiceRef,
        AcceptLatePaymentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(routeInvoiceRef))
        {
            return Error.Validation("Payment.InvalidInvoiceRef", "InvoiceRef không được rỗng.");
        }

        var validationResult = request.Validate();
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string normalizedInvoiceRef = routeInvoiceRef.Trim();
        string normalizedOperationKey = request.OperationKey.Trim();
        string normalizedPaymentRef = request.PaymentRef.Trim();
        string normalizedRecoveryId = request.RecoveryId.Trim();

        string fingerprint = PayloadFingerprinter.ComputeAcceptLatePaymentFingerprint(
            request.RentalOrderId,
            normalizedInvoiceRef,
            normalizedPaymentRef,
            normalizedRecoveryId);

        // 1. Check idempotency receipt first
        var existingReceipt = await _repository.GetOperationReceiptAsync(normalizedOperationKey, ct);
        if (existingReceipt is not null)
        {
            if (OperationReceiptReplayHelper.TryReplay<AcceptLatePaymentResponse>(
                existingReceipt,
                "AcceptLatePayment",
                fingerprint,
                parsed => parsed with { IsReplay = true },
                out var replayResult))
            {
                return replayResult;
            }

            return replayResult;
        }

        // 2. Request Integrity evidence outside the transaction (mandatory)
        var verified = await _integrityChecker.VerifyAsync(new TransactionVerificationRequest(
            PaymentRef: normalizedPaymentRef,
            Purpose: VerificationPurposes.LatePaymentAcceptance,
            CallerNonce: Guid.NewGuid().ToString("N"),
            InvoiceRef: normalizedInvoiceRef,
            ExpectedOrderId: request.RentalOrderId,
            ExpectedCurrency: "VND",
            CallerIdentity: $"rental-service:recovery:{normalizedRecoveryId}"), ct);

        if (verified.IsFailure)
        {
            // When verification fails because a concurrent request with the same key just completed,
            // re-read the receipt and return replay if fingerprint matches
            var racedReceiptOnFailure = await _repository.GetOperationReceiptAsync(normalizedOperationKey, ct);
            if (racedReceiptOnFailure is not null &&
                OperationReceiptReplayHelper.TryReplay<AcceptLatePaymentResponse>(
                    racedReceiptOnFailure,
                    "AcceptLatePayment",
                    fingerprint,
                    parsed => parsed with { IsReplay = true },
                    out var raceReplay))
            {
                return raceReplay;
            }

            return verified.Error;
        }

        var evidenceResult = VerifiedWriteContext.FromResponse(verified.Value, VerificationPurposes.LatePaymentAcceptance, _timeProvider.GetUtcNow());
        if (evidenceResult.IsFailure)
        {
            return evidenceResult.Error;
        }
        var verifiedEvidence = evidenceResult.Value;
        // 3. Open transaction and acquire row locks in consistent order
        await using var tx = await _repository.BeginTransactionAsync(ct);
        try
        {
            // Lock invoice first
            var invoice = await _repository.FindInvoiceWithRowLockAsync(normalizedInvoiceRef, ct);
            if (invoice is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn cần chấp nhận thanh toán trễ.");
            }

            // Lock payment transaction
            var pt = await _repository.FindPaymentTransactionByPaymentRefWithRowLockAsync(normalizedPaymentRef, ct);
            if (pt is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("PAYMENT_TRANSACTION_NOT_FOUND", "Không tìm thấy giao dịch ngân hàng tương ứng với PaymentRef.");
            }

            // Stream lock on operation key
            await _repository.ExecuteStreamLockAsync("operation:" + normalizedOperationKey, ct);

            // Re-check receipt under lock
            var racedReceipt = await _repository.GetOperationReceiptAsync(normalizedOperationKey, ct);
            if (racedReceipt is not null)
            {
                await tx.RollbackAsync(ct);
                if (OperationReceiptReplayHelper.TryReplay<AcceptLatePaymentResponse>(
                    racedReceipt,
                    "AcceptLatePayment",
                    fingerprint,
                    parsed => parsed with { IsReplay = true },
                    out var racedReplay))
                {
                    return racedReplay;
                }
                return racedReplay;
            }

            // 4. Validate all business invariants under lock using LatePaymentAcceptancePolicy
            var eligResult = LatePaymentAcceptancePolicy.ValidateEligibility(
                invoice,
                request.RentalOrderId,
                normalizedPaymentRef,
                _timeProvider.GetUtcNow());
            if (eligResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return eligResult.Error;
            }

            var ptResult = LatePaymentAcceptancePolicy.ValidatePaymentTransaction(invoice, pt);
            if (ptResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return ptResult.Error;
            }

            var existingRefunds = await _refundRepository.GetForInvoiceAsync(invoice.Id, ct);
            var refundResult = LatePaymentAcceptancePolicy.ValidateNoRefundReservation(existingRefunds.Count);
            if (refundResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return refundResult.Error;
            }

            // Validate evidence claims under lock against locked rows
            var invoiceClaimBefore = OperationalSnapshotEngine.CreateInvoiceClaim(invoice, invoice.IntegritySnapshotVersion);
            var ptClaimBefore = OperationalSnapshotEngine.CreatePaymentTransactionClaim(pt);
            var requiredClaims = new[]
            {
                new VerifiedWriteClaim(invoiceClaimBefore.EntityType, invoiceClaimBefore.EntityId, invoiceClaimBefore.Revision, invoiceClaimBefore.Digest),
                new VerifiedWriteClaim(ptClaimBefore.EntityType, ptClaimBefore.EntityId, ptClaimBefore.Revision, ptClaimBefore.Digest)
            };
            var guard = verifiedEvidence.Validate(_timeProvider.GetUtcNow(), requiredClaims);
            if (guard.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return guard.Error;
            }

            var utcNow = _timeProvider.GetUtcNow().ToUniversalTime();

            // 5. Apply transitions
            var acceptResult = invoice.MarkLatePaymentAccepted(utcNow);
            if (acceptResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return acceptResult.Error;
            }

            pt.AcceptLatePayment(PaymentTransactionReason.LatePaymentAccepted);

            var response = new AcceptLatePaymentResponse(
                invoice.InvoiceRef,
                invoice.Status,
                IsReplay: false,
                utcNow,
                invoice.RentalOrderId,
                invoice.PaymentRef,
                normalizedRecoveryId);

            var receipt = OperationReceipt.Create(
                normalizedOperationKey,
                "AcceptLatePayment",
                fingerprint,
                JsonSerializer.Serialize(response),
                invoice.InvoiceRef,
                invoice.RentalOrderId,
                utcNow);

            Guid sharedOperationId = Guid.NewGuid();

            var prevInvoiceJournal = await _repository.GetLatestJournalEntryAsync(invoice.InvoiceRef, ct);
            long nextInvoiceVersion = (prevInvoiceJournal?.Version ?? 0) + 1;
            string prevInvoiceHash = prevInvoiceJournal?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

            var prevPtJournal = await _repository.GetLatestJournalEntryAsync(pt.PaymentRef, ct);
            long nextPtVersion = (prevPtJournal?.Version ?? 0) + 1;
            string prevPtHash = prevPtJournal?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

            var invoiceClaim = OperationalSnapshotEngine.CreateInvoiceClaim(invoice, invoice.IntegritySnapshotVersion);
            var ptClaim = OperationalSnapshotEngine.CreatePaymentTransactionClaim(pt);
            var receiptClaim = OperationalSnapshotEngine.CreateOperationReceiptClaim(receipt);
            var claims = new List<CanonicalEntityClaim> { invoiceClaim, ptClaim, receiptClaim };

            var acceptInvoiceJournal = JournalEntryFactory.CreateEntry(
                operationId: sharedOperationId,
                streamId: invoice.InvoiceRef,
                streamType: JournalStreamTypes.Invoice,
                version: nextInvoiceVersion,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.PaymentMatched,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: invoice.Status,
                confirmationSource: JournalConfirmationSources.System,
                actor: $"RENTAL:RECOVERY:{normalizedRecoveryId}",
                timestampUtc: utcNow,
                previousHash: prevInvoiceHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["invoiceRef"] = invoice.InvoiceRef,
                    ["paymentRef"] = invoice.PaymentRef,
                    ["recoveryId"] = normalizedRecoveryId
                },
                snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision, invoice.IntegritySnapshotVersion),
                entityClaims: claims,
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: _encryptor);

            var acceptPtJournal = JournalEntryFactory.CreateEntry(
                operationId: sharedOperationId,
                streamId: pt.PaymentRef,
                streamType: JournalStreamTypes.PaymentTransaction,
                version: nextPtVersion,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.PaymentMatched,
                amount: pt.TransferAmount,
                currency: "VND",
                status: pt.Status,
                confirmationSource: JournalConfirmationSources.System,
                actor: $"RENTAL:RECOVERY:{normalizedRecoveryId}",
                timestampUtc: utcNow,
                previousHash: prevPtHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["invoiceRef"] = invoice.InvoiceRef,
                    ["paymentRef"] = invoice.PaymentRef,
                    ["recoveryId"] = normalizedRecoveryId
                },
                snapshot: OperationalSnapshotEngine.CreatePaymentTransactionSnapshot(pt, pt.Revision),
                entityClaims: claims,
                paymentRef: pt.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: _encryptor);

            await _repository.SaveLatePaymentAcceptanceAsync(invoice, pt, receipt, acceptInvoiceJournal, acceptPtJournal, ct);
            // Pre-commit check: ensure evidence and cutoff have not expired during processing
            var commitNow = _timeProvider.GetUtcNow();
            if (commitNow >= verifiedEvidence.ExpiresAt)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("Payment.ReceiptExpired", "Bằng chứng xác minh Integrity đã hết hạn trước khi commit.");
            }
            if (LatePaymentAcceptancePolicy.IsCutoffExpired(invoice.PaymentDeadline, commitNow))
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("LATE_PAYMENT_WINDOW_EXPIRED", "Đã quá hạn cutoff thanh toán trễ trước khi commit.");
            }

            await tx.CommitAsync(ct);
            if (_notifier is not null)
            {
                await _notifier.NotifyPaymentCompletedAsync(invoice.RenterId, acceptInvoiceJournal.EventId, invoice, ct);
            }

            return Result.Success(response);
        }
        catch (StorageConstraintViolationException ex)
        {
            await tx.RollbackAsync(ct);
            if (ex.ViolationType == StorageConstraintViolationType.OperationKey)
            {
                var raced = await _repository.GetOperationReceiptAsync(normalizedOperationKey, ct);
                if (raced is not null &&
                    OperationReceiptReplayHelper.TryReplay<AcceptLatePaymentResponse>(
                        raced,
                        "AcceptLatePayment",
                        fingerprint,
                        parsed => parsed with { IsReplay = true },
                        out var replayOutcome))
                {
                    return replayOutcome;
                }
                return Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã được sử dụng với payload khác.");
            }
            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
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
                        snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(locked, locked.Revision, locked.IntegritySnapshotVersion),
                        entityClaims: [OperationalSnapshotEngine.CreateInvoiceClaim(locked, locked.IntegritySnapshotVersion)],
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
