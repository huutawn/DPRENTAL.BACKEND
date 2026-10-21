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
    private readonly IOptionsMonitor<PaymentKafkaOptions>? _kafkaOptions;

    public IntegrityVerificationEngine(
        IntegrityDbContext integrityDb,
        PaymentServiceDbContext paymentDb,
        Dprental.PaymentService.Application.IServices.IIntegritySigningAdapter keyManager,
        IPaymentDataEncryptor encryptor,
        IOptionsMonitor<IntegrityVerificationOptions> options,
        TimeProvider? timeProvider = null,
        IOptionsMonitor<PaymentKafkaOptions>? kafkaOptions = null)
    {
        _integrityDb = integrityDb ?? throw new ArgumentNullException(nameof(integrityDb));
        _paymentDb = paymentDb ?? throw new ArgumentNullException(nameof(paymentDb));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _kafkaOptions = kafkaOptions;
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
        if (purpose is not (VerificationPurposes.PaymentConfirmation or VerificationPurposes.RefundRequest or VerificationPurposes.Settlement or VerificationPurposes.ManualRefundConfirmation or VerificationPurposes.OutboxPublication))
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
            OutboxMessage? publicationMessage = null;
            if (purpose == VerificationPurposes.OutboxPublication)
            {
                Guid msgId = request.OutboxMessageId ?? (Guid.TryParse(paymentRef, out var parsedGuid) ? parsedGuid : Guid.Empty);
                var outboxMsg = await _paymentDb.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.MessageId == msgId, cts.Token);
                publicationMessage = outboxMsg;
                if (outboxMsg is null)
                {
                    return CreateResponse(
                        request,
                        IntegrityStatuses.NotFound,
                        0,
                        null,
                        isUsable: false,
                        reason: "OUTBOX_MESSAGE_NOT_FOUND",
                        actualAmount: 0m,
                        actualCurrency: request.ExpectedCurrency ?? "VND",
                        actualStatus: "NOT_FOUND",
                        now: now);
                }

                if (outboxMsg.QuarantinedAt.HasValue)
                {
                    return CreateResponse(
                        request,
                        IntegrityStatuses.Tampered,
                        0,
                        null,
                        isUsable: false,
                        reason: "MESSAGE_QUARANTINED",
                        actualAmount: 0m,
                        actualCurrency: request.ExpectedCurrency ?? "VND",
                        actualStatus: "QUARANTINED",
                        now: now);
                }

                string computedDigest = CanonicalJournalHasher.ComputeOutboxDigest(outboxMsg.Payload);
                if (!string.IsNullOrWhiteSpace(request.ExpectedOutboxDigest) &&
                    !string.Equals(computedDigest, request.ExpectedOutboxDigest, StringComparison.OrdinalIgnoreCase))
                {
                    return CreateResponse(
                        request,
                        IntegrityStatuses.Tampered,
                        0,
                        null,
                        isUsable: false,
                        reason: "PAYLOAD_DIGEST_MISMATCH",
                        actualAmount: 0m,
                        actualCurrency: request.ExpectedCurrency ?? "VND",
                        actualStatus: "TAMPERED",
                        now: now);
                }

                if (outboxMsg.EventId.HasValue)
                {
                    entry = await _paymentDb.TransactionJournals.AsNoTracking()
                        .FirstOrDefaultAsync(j => j.EventId == outboxMsg.EventId.Value, cts.Token);
                }

                if (entry is null)
                {
                    entry = await _paymentDb.TransactionJournals.AsNoTracking()
                        .FirstOrDefaultAsync(j => j.OutboxDigest == computedDigest || j.OutboxMessageId == outboxMsg.MessageId, cts.Token);
                }

                if (entry is not null &&
                    (entry.OutboxMessageId != outboxMsg.MessageId ||
                     !string.Equals(entry.OutboxDigest, computedDigest, StringComparison.OrdinalIgnoreCase) ||
                     outboxMsg.EventId != entry.EventId || outboxMsg.OperationId != entry.OperationId ||
                     string.IsNullOrWhiteSpace(request.Topic) ||
                     !string.Equals(request.MessageKey, outboxMsg.AggregateId, StringComparison.Ordinal)))
                {
                    return CreateResponse(request, IntegrityStatuses.Tampered, entry.Version,
                        entry.ConfirmationSource, false, "OUTBOX_LINK_MISMATCH", entry.Amount,
                        entry.Currency, entry.Status, now);
                }

                if (entry is not null)
                {
                    manifest = await _integrityDb.Manifests.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.StreamId == entry.StreamId, cts.Token);
                }
            }
            else if (purpose == VerificationPurposes.ManualRefundConfirmation)
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
            else if (purpose is VerificationPurposes.Settlement or VerificationPurposes.RefundRequest)
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
            using var publicKey = EcdsaAttestation.ImportPublicKeyBase64(pubKeyB64);

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
                    .AsNoTracking().Include(x => x.Items)
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

                    }
                }
            }
            else if (purpose is VerificationPurposes.PaymentConfirmation or VerificationPurposes.Settlement or VerificationPurposes.RefundRequest)
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
                    if (!string.Equals(invoice.PaymentRef, request.PaymentRef.Trim(), StringComparison.Ordinal))
                    {
                        isUsable = false;
                        usabilityReason = "PAYMENT_REF_MISMATCH";
                    }
                    else if (invoice.Status != InvoiceStatus.Paid && !(purpose == VerificationPurposes.RefundRequest &&
                        invoice.Status is InvoiceStatus.Expired or InvoiceStatus.Cancelled && !string.IsNullOrWhiteSpace(invoice.PaymentRef)))
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
                                if (request.SupplierId.HasValue && item.SupplierId != request.SupplierId.Value)
                                {
                                    isUsable = false;
                                    usabilityReason = "SUPPLIER_MISMATCH";
                                }
                                var itemSnap = OperationalSnapshotEngine.CreateInvoiceItemSnapshot(item, item.Revision);
                                string itemDigest = OperationalSnapshotEngine.ComputeSnapshotDigest(itemSnap);
                                claimsList.Add(new VerificationEntityClaim("InvoiceItem", item.RentalItemId.ToString("D"), item.Revision, itemDigest));


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
            if (purpose == VerificationPurposes.RefundRequest && isUsable)
            {
                var refundInvoice = await _paymentDb.Invoices.AsNoTracking()
                    .FirstAsync(x => x.InvoiceRef == request.InvoiceRef, cts.Token);
                var transaction = await _paymentDb.PaymentTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.PaymentRef == request.PaymentRef, cts.Token);
                if (transaction is null || transaction.TransferAmount != refundInvoice.TotalAmount ||
                    transaction.Status is not (PaymentTransactionStatus.Matched or PaymentTransactionStatus.ManualRefundRequired))
                    return CreateResponse(request, IntegrityStatuses.NotFound, entry.Version, entry.ConfirmationSource,
                        false, "RECEIVED_PAYMENT_NOT_CONFIRMED", entry.Amount, entry.Currency, entry.Status, now);
                foreach (var item in await _paymentDb.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == refundInvoice.Id).ToListAsync(cts.Token))
                {
                    var claim = OperationalSnapshotEngine.CreateInvoiceItemClaim(item);
                    claimsList.Add(new VerificationEntityClaim(claim.EntityType, claim.EntityId, claim.Revision, claim.Digest));
                }
            }
            if (isUsable && !string.IsNullOrWhiteSpace(request.ExpectedStatus) &&
                !string.Equals(entry.Status, request.ExpectedStatus.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                isUsable = false;
                usabilityReason = "STATUS_MISMATCH";
            }
            foreach (var claim in claimsList)
            {
                var head = await _integrityDb.EntityHeads.AsNoTracking().FirstOrDefaultAsync(
                    h => h.EntityType == claim.EntityType && h.EntityId == claim.EntityId, cts.Token);
                if (head is null || claim.Revision > head.LatestRevision)
                    return CreateResponse(request, IntegrityStatuses.PendingConfirmation, entry.Version,
                        entry.ConfirmationSource, false, "AWAITING_ENTITY_CONFIRMATION", entry.Amount,
                        entry.Currency, entry.Status, now);
                if (claim.Revision < head.LatestRevision || !string.Equals(claim.Digest, head.LatestDigest, StringComparison.OrdinalIgnoreCase))
                    return CreateResponse(request, IntegrityStatuses.Tampered, entry.Version,
                        entry.ConfirmationSource, false, claim.Revision < head.LatestRevision ? "ROLLBACK_DETECTED" : "OPERATIONAL_SNAPSHOT_TAMPERED",
                        entry.Amount, entry.Currency, entry.Status, now);
            }
            PublicationProof? publicationProof = null;
            if (isUsable && publicationMessage is not null)
            {
                using var envelope = System.Text.Json.JsonDocument.Parse(publicationMessage.Payload);
                var body = envelope.RootElement;
                if (!body.TryGetProperty("aggregateId", out var aggregate) || aggregate.GetString() != publicationMessage.AggregateId ||
                    !body.TryGetProperty("messageType", out var type) || type.GetString() != publicationMessage.MessageType ||
                    !body.TryGetProperty("messageId", out var id) || id.GetGuid() != publicationMessage.MessageId ||
                    request.Topic != (_kafkaOptions?.CurrentValue.Topic ?? "payment.events.v1"))
                    return CreateResponse(request, IntegrityStatuses.Tampered, entry.Version,
                        entry.ConfirmationSource, false, "OUTBOX_ROUTING_MISMATCH", entry.Amount,
                        entry.Currency, entry.Status, now);
                var proofBody = new PublicationProofBody(1, _keyManager.Issuer, "rental-service",
                    _keyManager.Environment, _keyManager.KeyId, publicationMessage.MessageId,
                    entry.EventId, entry.OperationId, request.Topic, request.MessageKey!,
                    publicationMessage.MessageType, entry.OutboxDigest!, now);
                string proofPayload = proofBody.Serialize();
                publicationProof = new PublicationProof(proofPayload,
                    _keyManager.Sign(PublicationProof.DomainSeparator + proofPayload));
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
                entityClaims: claimsList,
                publicationProof: publicationProof);
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
        IReadOnlyList<VerificationEntityClaim>? entityClaims = null,
        PublicationProof? publicationProof = null)
    {
        string keyId = _keyManager.KeyId;
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
            expectedStatus: request.ExpectedStatus,
            outboxMessageId: request.OutboxMessageId);

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
            Receipt: receipt, PublicationProof: publicationProof));
    }
}
