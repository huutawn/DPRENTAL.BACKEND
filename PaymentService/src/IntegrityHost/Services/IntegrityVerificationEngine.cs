using System.Globalization;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.IntegrityHost.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityVerificationEngine
{
    private readonly IntegrityDbContext _integrityDb;
    private readonly PaymentServiceDbContext _paymentDb;
    private readonly IntegrityKeyManager _keyManager;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<IntegrityVerificationOptions> _options;

    public IntegrityVerificationEngine(
        IntegrityDbContext integrityDb,
        PaymentServiceDbContext paymentDb,
        IntegrityKeyManager keyManager,
        IOptionsMonitor<IntegrityVerificationOptions> options,
        TimeProvider? timeProvider = null)
    {
        _integrityDb = integrityDb ?? throw new ArgumentNullException(nameof(integrityDb));
        _paymentDb = paymentDb ?? throw new ArgumentNullException(nameof(paymentDb));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<TransactionVerificationResponse>> VerifyAsync(
        TransactionVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PaymentRef))
        {
            return Error.Validation("Payment.Verification.InvalidPaymentRef", "PaymentRef không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.CallerNonce))
        {
            return Error.Validation("Payment.Verification.InvalidNonce", "CallerNonce không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.Purpose))
        {
            return Error.Validation("Payment.Verification.InvalidPurpose", "Purpose không được để trống.");
        }

        string purpose = request.Purpose.Trim().ToUpperInvariant();
        if (purpose is not (VerificationPurposes.PaymentConfirmation or VerificationPurposes.Settlement or VerificationPurposes.ManualRefundConfirmation))
        {
            return Error.Validation("Payment.Verification.UnsupportedPurpose", $"Purpose '{request.Purpose}' không được hỗ trợ.");
        }

        var now = _timeProvider.GetUtcNow();
        int budgetMs = _options.CurrentValue.ServerBudgetMilliseconds > 0 ? _options.CurrentValue.ServerBudgetMilliseconds : 900;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));

        try
        {
            string paymentRef = request.PaymentRef.Trim();

            // 1. Check StreamManifest in Integrity DB
            var manifest = await _integrityDb.Manifests
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.StreamId == paymentRef || m.StreamId == request.InvoiceRef || m.StreamId == request.RequirementRef, cts.Token);

            if (manifest?.IsBlocked == true)
            {
                return CreateResponse(
                    request,
                    IntegrityStatuses.Tampered,
                    manifest.LatestVersion,
                    "STREAM_BLOCKED",
                    isUsable: false,
                    reason: "STREAM_BLOCKED",
                    actualAmount: 0m,
                    actualCurrency: request.ExpectedCurrency ?? "VND",
                    actualStatus: "BLOCKED",
                    now: now);
            }

            // 2. Query journal entry from Payment DB
            var entry = await _paymentDb.TransactionJournals
                .AsNoTracking()
                .Where(j => j.StreamId == paymentRef || j.PaymentRef == paymentRef || (request.InvoiceRef != null && j.InvoiceRef == request.InvoiceRef))
                .OrderByDescending(j => j.Version)
                .FirstOrDefaultAsync(cts.Token);

            if (entry is null)
            {
                // If manifest exists in evidence store but not in Payment DB -> ROLLBACK DETECTED!
                if (manifest is not null)
                {
                    return CreateResponse(
                        request,
                        IntegrityStatuses.Tampered,
                        manifest.LatestVersion,
                        "ROLLBACK_DETECTED",
                        isUsable: false,
                        reason: "ROLLBACK_DETECTED",
                        actualAmount: 0m,
                        actualCurrency: request.ExpectedCurrency ?? "VND",
                        actualStatus: "NOT_FOUND",
                        now: now);
                }

                return CreateResponse(
                    request,
                    IntegrityStatuses.NotFound,
                    0,
                    null,
                    isUsable: false,
                    reason: "PAYMENT_NOT_FOUND",
                    actualAmount: 0m,
                    actualCurrency: request.ExpectedCurrency ?? "VND",
                    actualStatus: "NOT_FOUND",
                    now: now);
            }

            // 3. Check if entry is confirmed
            if (!entry.IsConfirmed || string.IsNullOrWhiteSpace(entry.ProofSignature))
            {
                return CreateResponse(
                    request,
                    IntegrityStatuses.PendingConfirmation,
                    entry.Version,
                    entry.ConfirmationSource,
                    isUsable: false,
                    reason: "PENDING_CONFIRMATION",
                    actualAmount: entry.Amount,
                    actualCurrency: entry.Currency,
                    actualStatus: entry.Status,
                    now: now,
                    confirmedAt: entry.ConfirmedAt,
                    signingKeyId: entry.SigningKeyId,
                    proofSignature: entry.ProofSignature);
            }

            // 4. Verify evidence in Integrity DB
            var attestation = await _integrityDb.Attestations
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.StreamId == entry.StreamId && a.Version == entry.Version, cts.Token);

            if (attestation is null || !string.Equals(attestation.EntryHash, entry.EntryHash, StringComparison.OrdinalIgnoreCase))
            {
                return CreateResponse(
                    request,
                    IntegrityStatuses.Tampered,
                    entry.Version,
                    entry.ConfirmationSource,
                    isUsable: false,
                    reason: "ATTESTATION_MISMATCH",
                    actualAmount: entry.Amount,
                    actualCurrency: entry.Currency,
                    actualStatus: entry.Status,
                    now: now);
            }

            // 5. Verify Hash computation
            string envelopeDigest = CanonicalJournalCodec.ComputeEnvelopeDigest(
                entry.EncryptedPayload, entry.PayloadNonce, entry.PayloadTag, entry.EncryptionKeyId);

            string expectedEntryHash = CanonicalJournalCodec.ComputeEntryHash(
                entry.StreamId, entry.Version, entry.EventId, entry.PreviousHash, entry.PayloadHash, envelopeDigest);
            if (!string.Equals(entry.EntryHash, expectedEntryHash, StringComparison.OrdinalIgnoreCase))
            {
                return CreateResponse(
                    request,
                    IntegrityStatuses.Tampered,
                    entry.Version,
                    entry.ConfirmationSource,
                    isUsable: false,
                    reason: "HASH_TAMPERED",
                    actualAmount: entry.Amount,
                    actualCurrency: entry.Currency,
                    actualStatus: entry.Status,
                    now: now);
            }

            // 6. Verify signature against public key
            var publicKey = EcdsaAttestation.ImportPublicKeyBase64(_keyManager.PublicKeyBase64);
            string attestationMessage = EcdsaAttestation.BuildAttestationMessage(
                entry.SigningKeyId ?? _keyManager.KeyId,
                entry.StreamId,
                entry.Version,
                entry.EventId,
                entry.EntryHash,
                entry.ConfirmedAt ?? now);

            bool sigValid = EcdsaAttestation.Verify(publicKey, attestationMessage, entry.ProofSignature);
            if (!sigValid)
            {
                return CreateResponse(
                    request,
                    IntegrityStatuses.Tampered,
                    entry.Version,
                    entry.ConfirmationSource,
                    isUsable: false,
                    reason: "SIGNATURE_INVALID",
                    actualAmount: entry.Amount,
                    actualCurrency: entry.Currency,
                    actualStatus: entry.Status,
                    now: now);
            }

            // 7. Purpose-specific business usability verification against actual operational data
            bool isUsable = true;
            string usabilityReason = "OK";

            if (purpose is VerificationPurposes.PaymentConfirmation or VerificationPurposes.Settlement)
            {
                Invoice? invoice = null;
                if (!string.IsNullOrWhiteSpace(request.InvoiceRef))
                {
                    invoice = await _paymentDb.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceRef == request.InvoiceRef, cts.Token);
                }
                else if (entry.RentalOrderId.HasValue)
                {
                    invoice = await _paymentDb.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.RentalOrderId == entry.RentalOrderId.Value, cts.Token);
                }

                if (invoice is null)
                {
                    isUsable = false;
                    usabilityReason = "INVOICE_NOT_FOUND";
                }
                else
                {
                    // Invoice must be PAID
                    if (!string.Equals(invoice.Status, InvoiceStatus.Paid, StringComparison.Ordinal))
                    {
                        isUsable = false;
                        usabilityReason = "INVOICE_NOT_PAID";
                    }
                    else if (request.ExpectedAmount.HasValue && invoice.TotalAmount != request.ExpectedAmount.Value)
                    {
                        isUsable = false;
                        usabilityReason = "AMOUNT_MISMATCH";
                    }
                    else if (!string.IsNullOrWhiteSpace(request.ExpectedCurrency) &&
                             !string.Equals(invoice.Currency, request.ExpectedCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        isUsable = false;
                        usabilityReason = "CURRENCY_MISMATCH";
                    }
                    else if (request.ExpectedOrderId.HasValue && invoice.RentalOrderId != request.ExpectedOrderId.Value)
                    {
                        isUsable = false;
                        usabilityReason = "RENTAL_ORDER_MISMATCH";
                    }
                    else if (request.ExpectedRenterId.HasValue && invoice.RenterId != request.ExpectedRenterId.Value)
                    {
                        isUsable = false;
                        usabilityReason = "RENTER_MISMATCH";
                    }
                }
            }
            else if (purpose == VerificationPurposes.ManualRefundConfirmation)
            {
                if (string.IsNullOrWhiteSpace(request.RequirementRef))
                {
                    isUsable = false;
                    usabilityReason = "REQUIREMENT_REF_REQUIRED";
                }
                else
                {
                    var refund = await _paymentDb.ManualRefundRequirements
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.RequirementRef == request.RequirementRef, cts.Token);

                    if (refund is null)
                    {
                        isUsable = false;
                        usabilityReason = "REFUND_REQUIREMENT_NOT_FOUND";
                    }
                    else if (refund.Status != ManualRefundStatus.PendingManualRefund)
                    {
                        isUsable = false;
                        usabilityReason = "REFUND_NOT_PENDING";
                    }
                    else if (request.ExpectedAmount.HasValue && refund.Amount != request.ExpectedAmount.Value)
                    {
                        isUsable = false;
                        usabilityReason = "AMOUNT_MISMATCH";
                    }
                }
            }

            return CreateResponse(
                request,
                IntegrityStatuses.Valid,
                entry.Version,
                entry.ConfirmationSource,
                isUsable: isUsable,
                reason: usabilityReason,
                actualAmount: entry.Amount,
                actualCurrency: entry.Currency,
                actualStatus: entry.Status,
                now: now,
                confirmedAt: entry.ConfirmedAt,
                signingKeyId: entry.SigningKeyId,
                proofSignature: entry.ProofSignature,
                entryHash: entry.EntryHash);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error.Failure("Payment.Verification.Timeout", "Quá thời gian xử lý kiểm chứng trong ngân sách 900ms.");
        }
    }

    private Result<TransactionVerificationResponse> CreateResponse(
        TransactionVerificationRequest request,
        string integrityStatus,
        long version,
        string? confirmationSource,
        bool isUsable,
        string reason,
        decimal actualAmount,
        string actualCurrency,
        string actualStatus,
        DateTimeOffset now,
        DateTimeOffset? confirmedAt = null,
        string? signingKeyId = null,
        string? proofSignature = null,
        string? entryHash = null)
    {
        string keyId = signingKeyId ?? _keyManager.KeyId;
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(
            request.PaymentRef,
            request.Purpose,
            request.CallerNonce,
            request.ExpectedAmount,
            request.ExpectedCurrency,
            request.ExpectedStatus,
            request.InvoiceRef,
            request.ExpectedOrderId,
            request.ExpectedRenterId,
            request.RentalItemId,
            request.SupplierId,
            request.RequirementRef);

        DateTimeOffset issuedAt = now.ToUniversalTime();
        DateTimeOffset expiresAt = issuedAt.AddSeconds(5);

        string receiptMessage = EcdsaAttestation.BuildVerificationReceiptMessage(
            keyId,
            requestHash,
            request.CallerNonce,
            integrityStatus,
            isUsable,
            reason,
            issuedAt,
            expiresAt);

        string receiptSignature = _keyManager.Sign(receiptMessage);

        var receipt = new VerificationReceipt(
            ReceiptId: Guid.NewGuid(),
            KeyId: keyId,
            RequestHash: requestHash,
            CallerNonce: request.CallerNonce,
            IntegrityStatus: integrityStatus,
            IsUsable: isUsable,
            Reason: reason,
            IssuedAt: issuedAt,
            ExpiresAt: expiresAt,
            Signature: receiptSignature);

        string boundProof = $"{EcdsaAttestation.AttestationPrefix}:{keyId}:{request.PaymentRef}:{version}:{entryHash ?? string.Empty}:{confirmedAt?.ToUnixTimeMilliseconds()}";

        return Result.Success(new TransactionVerificationResponse(
            new TransactionIntegrityResult(
                Status: integrityStatus,
                Version: version,
                ConfirmationSource: confirmationSource,
                ConfirmedAt: confirmedAt,
                KeyId: keyId,
                Signature: proofSignature,
                Proof: boundProof,
                CallerNonce: request.CallerNonce),
            new TransactionUsabilityResult(
                IsUsable: isUsable,
                Reason: reason,
                ActualAmount: actualAmount,
                ActualCurrency: actualCurrency,
                ActualStatus: actualStatus),
            Receipt: receipt));
    }
}
