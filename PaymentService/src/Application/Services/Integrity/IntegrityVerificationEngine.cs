using System.Globalization;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Entities.Integrity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services.Integrity;

public sealed class IntegrityVerificationEngine : Dprental.PaymentService.Application.IServices.IIntegrityVerificationService
{
    private readonly IntegrityDbContext _integrityDb;
    private readonly PaymentServiceDbContext _paymentDb;
    private readonly Dprental.PaymentService.Application.IServices.IIntegritySigningAdapter _keyManager;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<IntegrityVerificationOptions> _options;
    private readonly IPaymentDataEncryptor _encryptor;

    public IntegrityVerificationEngine(
        IntegrityDbContext integrityDb,
        PaymentServiceDbContext paymentDb,
        Dprental.PaymentService.Application.IServices.IIntegritySigningAdapter keyManager,
        IPaymentDataEncryptor encryptor,
        IOptionsMonitor<IntegrityVerificationOptions> options,
        TimeProvider? timeProvider = null)
    {
        _integrityDb = integrityDb ?? throw new ArgumentNullException(nameof(integrityDb));
        _paymentDb = paymentDb ?? throw new ArgumentNullException(nameof(paymentDb));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
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

            // 1. Resolve exact StreamManifest & JournalEntry by Purpose (no loose OR queries)
            TransactionJournalEntry? entry = null;
            StreamManifest? manifest = null;

            if (purpose == VerificationPurposes.ManualRefundConfirmation)
            {
                string reqRef = !string.IsNullOrWhiteSpace(request.RequirementRef) ? request.RequirementRef.Trim() : paymentRef;
                manifest = await _integrityDb.Manifests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.StreamId == reqRef, cts.Token);

                entry = await _paymentDb.TransactionJournals
                    .AsNoTracking()
                    .Where(j => j.StreamId == reqRef && j.StreamType == JournalStreamTypes.ManualRefund)
                    .OrderByDescending(j => j.Version)
                    .FirstOrDefaultAsync(cts.Token);
            }
            else if (purpose == VerificationPurposes.Settlement)
            {
                string invRef = !string.IsNullOrWhiteSpace(request.InvoiceRef) ? request.InvoiceRef.Trim() : paymentRef;
                manifest = await _integrityDb.Manifests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.StreamId == invRef, cts.Token);

                entry = await _paymentDb.TransactionJournals
                    .AsNoTracking()
                    .Where(j => j.StreamId == invRef && j.StreamType == JournalStreamTypes.Invoice)
                    .OrderByDescending(j => j.Version)
                    .FirstOrDefaultAsync(cts.Token);

                if (entry is null)
                {
                    entry = await _paymentDb.TransactionJournals
                        .AsNoTracking()
                        .Where(j => j.StreamId == paymentRef || j.PaymentRef == paymentRef)
                        .OrderByDescending(j => j.Version)
                        .FirstOrDefaultAsync(cts.Token);
                }
            }
            else
            {
                manifest = await _integrityDb.Manifests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.StreamId == paymentRef, cts.Token);

                entry = await _paymentDb.TransactionJournals
                    .AsNoTracking()
                    .Where(j => j.StreamId == paymentRef)
                    .OrderByDescending(j => j.Version)
                    .FirstOrDefaultAsync(cts.Token);

                if (entry is null && !string.IsNullOrWhiteSpace(request.InvoiceRef))
                {
                    entry = await _paymentDb.TransactionJournals
                        .AsNoTracking()
                        .Where(j => j.StreamId == request.InvoiceRef.Trim())
                        .OrderByDescending(j => j.Version)
                        .FirstOrDefaultAsync(cts.Token);
                }
            }

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
            // 5. Verify using unified JournalContentVerifier
            var verifyResult = JournalContentVerifier.Verify(entry, _encryptor);
            if (!verifyResult.IsValid)
            {
                string failureReason = verifyResult.FailureReason switch
                {
                    "ENTRY_HASH_MISMATCH" => "HASH_TAMPERED",
                    "ENVELOPE_DIGEST_MISMATCH" => "ENVELOPE_TAMPERED",
                    "DECRYPTION_FAILED" => "CIPHERTEXT_TAMPERED",
                    _ => (verifyResult.FailureReason ?? "PAYLOAD_HASH_TAMPERED")
                };

                return CreateResponse(
                    request,
                    IntegrityStatuses.Tampered,
                    entry.Version,
                    entry.ConfirmationSource,
                    isUsable: false,
                    reason: failureReason,
                    actualAmount: entry.Amount,
                    actualCurrency: entry.Currency,
                    actualStatus: entry.Status,
                    now: now);
            }

            // 6. Verify signature against public key (V2 first, fallback to V1)
            string signingKeyId = entry.SigningKeyId ?? _keyManager.KeyId;
            string? pubKeyB64 = _keyManager.GetPublicKey(signingKeyId) ?? _keyManager.PublicKeyBase64;
            var publicKey = EcdsaAttestation.ImportPublicKeyBase64(pubKeyB64);

            string attestationMessageV2 = EcdsaAttestation.BuildAttestationMessageV2(
                signingKeyId,
                _keyManager.Environment,
                entry.StreamId,
                entry.Version,
                entry.EventId,
                entry.EntryHash,
                entry.ConfirmedAt ?? now);

            bool sigValid = EcdsaAttestation.Verify(publicKey, attestationMessageV2, entry.ProofSignature);
            if (!sigValid)
            {
                string attestationMessageV1 = EcdsaAttestation.BuildAttestationMessage(
                    signingKeyId,
                    entry.StreamId,
                    entry.Version,
                    entry.EventId,
                    entry.EntryHash,
                    entry.ConfirmedAt ?? now);
                sigValid = EcdsaAttestation.Verify(publicKey, attestationMessageV1, entry.ProofSignature);
            }

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
            var claimsList = new List<VerificationEntityClaim>();

            if (purpose == VerificationPurposes.ManualRefundConfirmation)
            {
                string reqRef = !string.IsNullOrWhiteSpace(request.RequirementRef) ? request.RequirementRef.Trim() : paymentRef;
                var refund = await _paymentDb.ManualRefundRequirements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.RequirementRef == reqRef, cts.Token);

                if (refund is null)
                {
                    isUsable = false;
                    usabilityReason = "REFUND_REQUIREMENT_NOT_FOUND";
                }
                else
                {
                    var snap = OperationalSnapshotEngine.CreateManualRefundSnapshot(refund, refund.Revision);
                    string d = OperationalSnapshotEngine.ComputeSnapshotDigest(snap);
                    claimsList.Add(new VerificationEntityClaim("ManualRefundRequirement", refund.RequirementRef, refund.Revision, d));

                    if (refund.Status != ManualRefundStatus.PendingManualRefund)
                    {
                        if (refund.Status == ManualRefundStatus.Completed)
                        {
                            isUsable = true;
                            usabilityReason = "OK_REPLAY";
                        }
                        else
                        {
                            isUsable = false;
                            usabilityReason = "REFUND_NOT_PENDING";
                        }
                    }
                    else if (request.ExpectedAmount.HasValue && refund.Amount != request.ExpectedAmount.Value)
                    {
                        isUsable = false;
                        usabilityReason = "AMOUNT_MISMATCH";
                    }
                    else
                    {
                        var refundHead = await _integrityDb.EntityHeads
                            .AsNoTracking()
                            .FirstOrDefaultAsync(h => h.EntityType == "ManualRefundRequirement" && h.EntityId == refund.RequirementRef, cts.Token);

                        if (refundHead is not null)
                        {
                            if (refund.Revision < refundHead.LatestRevision)
                            {
                                return CreateResponse(
                                    request,
                                    IntegrityStatuses.Tampered,
                                    entry.Version,
                                    entry.ConfirmationSource,
                                    isUsable: false,
                                    reason: "ROLLBACK_DETECTED",
                                    actualAmount: entry.Amount,
                                    actualCurrency: entry.Currency,
                                    actualStatus: entry.Status,
                                    now: now,
                                    entityClaims: claimsList);
                            }
                            if (refund.Revision == refundHead.LatestRevision && !string.Equals(d, refundHead.LatestDigest, StringComparison.OrdinalIgnoreCase))
                            {
                                return CreateResponse(
                                    request,
                                    IntegrityStatuses.Tampered,
                                    entry.Version,
                                    entry.ConfirmationSource,
                                    isUsable: false,
                                    reason: "OPERATIONAL_SNAPSHOT_TAMPERED",
                                    actualAmount: entry.Amount,
                                    actualCurrency: entry.Currency,
                                    actualStatus: entry.Status,
                                    now: now,
                                    entityClaims: claimsList);
                            }
                        }
                    }
                }
            }
            else if (purpose is VerificationPurposes.PaymentConfirmation or VerificationPurposes.Settlement)
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
                    var snap = OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision);
                    string d = OperationalSnapshotEngine.ComputeSnapshotDigest(snap);
                    claimsList.Add(new VerificationEntityClaim("Invoice", invoice.InvoiceRef, invoice.Revision, d));

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
                    else
                    {
                        var invHead = await _integrityDb.EntityHeads
                            .AsNoTracking()
                            .FirstOrDefaultAsync(h => h.EntityType == "Invoice" && h.EntityId == invoice.InvoiceRef, cts.Token);

                        if (invHead is not null)
                        {
                            if (invoice.Revision < invHead.LatestRevision)
                            {
                                return CreateResponse(
                                    request,
                                    IntegrityStatuses.Tampered,
                                    entry.Version,
                                    entry.ConfirmationSource,
                                    isUsable: false,
                                    reason: "ROLLBACK_DETECTED",
                                    actualAmount: entry.Amount,
                                    actualCurrency: entry.Currency,
                                    actualStatus: entry.Status,
                                    now: now,
                                    entityClaims: claimsList);
                            }
                            if (invoice.Revision == invHead.LatestRevision && !string.Equals(d, invHead.LatestDigest, StringComparison.OrdinalIgnoreCase))
                            {
                                return CreateResponse(
                                    request,
                                    IntegrityStatuses.Tampered,
                                    entry.Version,
                                    entry.ConfirmationSource,
                                    isUsable: false,
                                    reason: "OPERATIONAL_SNAPSHOT_TAMPERED",
                                    actualAmount: entry.Amount,
                                    actualCurrency: entry.Currency,
                                    actualStatus: entry.Status,
                                    now: now,
                                    entityClaims: claimsList);
                            }
                        }

                        if (request.RentalItemId.HasValue)
                        {
                            var item = await _paymentDb.InvoiceItems.AsNoTracking()
                                .FirstOrDefaultAsync(ii => ii.InvoiceId == invoice.Id && ii.RentalItemId == request.RentalItemId.Value, cts.Token);
                            if (item is null)
                            {
                                isUsable = false;
                                usabilityReason = "INVOICE_ITEM_NOT_FOUND";
                            }
                            else
                            {
                                var itemSnap = OperationalSnapshotEngine.CreateInvoiceItemSnapshot(item, item.Revision);
                                string itemDigest = OperationalSnapshotEngine.ComputeSnapshotDigest(itemSnap);
                                claimsList.Add(new VerificationEntityClaim("InvoiceItem", item.RentalItemId.ToString("D"), item.Revision, itemDigest));

                                var itemHead = await _integrityDb.EntityHeads
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(h => h.EntityType == "InvoiceItem" && h.EntityId == item.RentalItemId.ToString("D"), cts.Token);

                                if (itemHead is not null)
                                {
                                    if (item.Revision == itemHead.LatestRevision && !string.Equals(itemDigest, itemHead.LatestDigest, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return CreateResponse(
                                            request,
                                            IntegrityStatuses.Tampered,
                                            entry.Version,
                                            entry.ConfirmationSource,
                                            isUsable: false,
                                            reason: "OPERATIONAL_SNAPSHOT_TAMPERED",
                                            actualAmount: entry.Amount,
                                            actualCurrency: entry.Currency,
                                            actualStatus: entry.Status,
                                            now: now,
                                            entityClaims: claimsList);
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(invoice.PaymentRef))
                        {
                            var pt = await _paymentDb.PaymentTransactions.AsNoTracking()
                                .FirstOrDefaultAsync(t => t.PaymentRef == invoice.PaymentRef, cts.Token);
                            if (pt is not null)
                            {
                                var ptSnap = OperationalSnapshotEngine.CreatePaymentTransactionSnapshot(pt, pt.Revision);
                                string ptDigest = OperationalSnapshotEngine.ComputeSnapshotDigest(ptSnap);
                                claimsList.Add(new VerificationEntityClaim("PaymentTransaction", pt.PaymentRef, pt.Revision, ptDigest));

                                var ptHead = await _integrityDb.EntityHeads
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(h => h.EntityType == "PaymentTransaction" && h.EntityId == pt.PaymentRef, cts.Token);

                                if (ptHead is not null)
                                {
                                    if (pt.Revision == ptHead.LatestRevision && !string.Equals(ptDigest, ptHead.LatestDigest, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return CreateResponse(
                                            request,
                                            IntegrityStatuses.Tampered,
                                            entry.Version,
                                            entry.ConfirmationSource,
                                            isUsable: false,
                                            reason: "OPERATIONAL_SNAPSHOT_TAMPERED",
                                            actualAmount: entry.Amount,
                                            actualCurrency: entry.Currency,
                                            actualStatus: entry.Status,
                                            now: now,
                                            entityClaims: claimsList);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                if (entry.StreamType == "PaymentTransaction" || !string.IsNullOrWhiteSpace(request.PaymentRef))
                {
                    var pt = await _paymentDb.PaymentTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.PaymentRef == (request.PaymentRef ?? entry.StreamId), cts.Token);
                    if (pt is not null)
                    {
                        var snap = OperationalSnapshotEngine.CreatePaymentTransactionSnapshot(pt, pt.Revision);
                        string d = OperationalSnapshotEngine.ComputeSnapshotDigest(snap);
                        claimsList.Add(new VerificationEntityClaim("PaymentTransaction", pt.PaymentRef, pt.Revision, d));
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
                entryHash: entry.EntryHash,
                entityClaims: claimsList);
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
        string? entryHash = null,
        IReadOnlyList<VerificationEntityClaim>? entityClaims = null)
    {
        string keyId = signingKeyId ?? _keyManager.KeyId;
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(
            paymentRef: request.PaymentRef,
            purpose: request.Purpose,
            callerNonce: request.CallerNonce,
            callerIdentity: request.CallerIdentity,
            expectedAmount: request.ExpectedAmount,
            expectedCurrency: request.ExpectedCurrency,
            invoiceRef: request.InvoiceRef,
            expectedOrderId: request.ExpectedOrderId,
            expectedRenterId: request.ExpectedRenterId,
            rentalItemId: request.RentalItemId,
            supplierId: request.SupplierId,
            requirementRef: request.RequirementRef,
            expectedOutboxDigest: request.ExpectedOutboxDigest,
            topic: request.Topic,
            messageKey: request.MessageKey,
            expectedStatus: request.ExpectedStatus);

        DateTimeOffset issuedAt = now.ToUniversalTime();
        DateTimeOffset expiresAt = issuedAt.AddSeconds(5);

        var claims = entityClaims ?? [];
        string claimsDigest = EcdsaAttestation.ComputeClaimsDigest(
            claims.Select(c => (c.EntityType, c.EntityId, c.Revision, c.Digest)));

        var body = new VerificationReceiptBody(
            Schema: 2,
            Issuer: _keyManager.Issuer,
            Audience: _keyManager.Audience,
            Environment: _keyManager.Environment,
            ReceiptId: Guid.NewGuid(),
            KeyId: keyId,
            RequestHash: requestHash,
            CallerIdentity: request.CallerIdentity ?? string.Empty,
            CallerNonce: request.CallerNonce,
            Purpose: request.Purpose,
            IntegrityStatus: integrityStatus,
            IsUsable: isUsable,
            Reason: reason,
            ActualAmount: actualAmount,
            ActualCurrency: actualCurrency,
            ActualStatus: actualStatus,
            EntityClaims: claims,
            IssuedAt: issuedAt,
            ExpiresAt: expiresAt);

        string receiptMessage = EcdsaAttestation.BuildVerificationReceiptMessageV2(
            body.Schema,
            body.Issuer,
            body.Audience,
            body.Environment,
            body.ReceiptId,
            body.KeyId,
            body.RequestHash,
            body.CallerIdentity,
            body.CallerNonce,
            body.Purpose,
            body.IntegrityStatus,
            body.IsUsable,
            body.Reason,
            body.ActualAmount,
            body.ActualCurrency,
            body.ActualStatus,
            claimsDigest,
            body.IssuedAt,
            body.ExpiresAt);

        string receiptSignature = _keyManager.Sign(receiptMessage);
        var receipt = new VerificationReceipt(body, receiptSignature);

        string boundProof = $"{EcdsaAttestation.AttestationPrefixV2}:{keyId}:{request.PaymentRef}:{version}:{entryHash ?? string.Empty}:{confirmedAt?.ToUnixTimeMilliseconds()}";

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
