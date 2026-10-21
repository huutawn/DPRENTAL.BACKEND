#pragma warning disable CA1848
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities.Integrity;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.PaymentService.Application.Services.Integrity;

public sealed class IntegrityConfirmationService : IIntegrityConfirmationService
{
    private readonly IIntegrityEvidenceRepository _evidenceRepo;
    private readonly ITransactionJournalRepository _paymentRepo;
    private readonly IIntegritySigningAdapter _keyManager;
    private readonly IPaymentDataEncryptor _encryptor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrityConfirmationService>? _logger;
    private readonly int _batchSize;

    public IntegrityConfirmationService(
        IIntegrityEvidenceRepository evidenceRepo,
        ITransactionJournalRepository paymentRepo,
        IIntegritySigningAdapter keyManager,
        IPaymentDataEncryptor encryptor,
        TimeProvider? timeProvider = null,
        ILogger<IntegrityConfirmationService>? logger = null,
        int batchSize = 100)
    {
        _evidenceRepo = evidenceRepo ?? throw new ArgumentNullException(nameof(evidenceRepo));
        _paymentRepo = paymentRepo ?? throw new ArgumentNullException(nameof(paymentRepo));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _batchSize = batchSize > 0 ? batchSize : 100;
    }

    public async Task<int> ProcessConfirmationBatchAsync(CancellationToken cancellationToken = default)
    {
        var pendingEntries = await _paymentRepo.GetPendingConfirmationEntriesAsync(_batchSize, cancellationToken);
        if (pendingEntries.Count == 0)
        {
            return 0;
        }

        int confirmedCount = 0;
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in pendingEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var manifest = await _evidenceRepo.GetManifestAsync(entry.StreamId, cancellationToken);
            if (manifest?.IsBlocked == true)
            {
                _logger?.LogWarning("Skipping entry {EntryId} because stream {StreamId} is blocked: {Reason}",
                    entry.Id, entry.StreamId, manifest.BlockedReason);
                continue;
            }

            // 1. Verify sequence order
            if (manifest is null)
            {
                if (entry.Version != 1 || !string.Equals(entry.PreviousHash, CanonicalJournalHasher.GenesisHash, StringComparison.OrdinalIgnoreCase))
                {
                    await RecordIncidentAndBlockAsync(
                        entry.StreamId,
                        "CHAIN_BROKEN",
                        entry.Version,
                        entry.EventId,
                        $"Stream {entry.StreamId} started at version {entry.Version} with non-genesis hash {entry.PreviousHash}.",
                        now,
                        cancellationToken);
                    continue;
                }
            }
            else
            {
                if (entry.Version == manifest.LatestVersion)
                {
                    // Check for idempotent retry
                    var existingAttestation = await _evidenceRepo.GetAttestationAsync(entry.StreamId, entry.Version, cancellationToken);
                    if (existingAttestation is not null)
                    {
                        if (string.Equals(existingAttestation.EntryHash, entry.EntryHash, StringComparison.OrdinalIgnoreCase))
                        {
                            // Replay existing attestation
                            await _paymentRepo.UpdateConfirmationAsync(
                                entry.Id,
                                existingAttestation.Signature,
                                existingAttestation.KeyId,
                                existingAttestation.ConfirmedAt,
                                cancellationToken);
                            confirmedCount++;
                            continue;
                        }
                        else
                        {
                            await RecordIncidentAndBlockAsync(
                                entry.StreamId,
                                "HASH_MISMATCH",
                                entry.Version,
                                entry.EventId,
                                $"Conflicting entry hash for existing version {entry.Version}.",
                                now,
                                cancellationToken);
                            continue;
                        }
                    }
                }

                if (entry.Version != manifest.LatestVersion + 1)
                {
                    await RecordIncidentAndBlockAsync(
                        entry.StreamId,
                        "VERSION_GAP",
                        entry.Version,
                        entry.EventId,
                        $"Version sequence gap: expected {manifest.LatestVersion + 1}, received {entry.Version}.",
                        now,
                        cancellationToken);
                    continue;
                }

                if (!string.Equals(entry.PreviousHash, manifest.LatestEntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    await RecordIncidentAndBlockAsync(
                        entry.StreamId,
                        "CHAIN_BROKEN",
                        entry.Version,
                        entry.EventId,
                        $"PreviousHash {entry.PreviousHash} does not match latest manifest hash {manifest.LatestEntryHash}.",
                        now,
                        cancellationToken);
                    continue;
                }
            }

            // 2. Use unified JournalContentVerifier
            var verifyResult = JournalContentVerifier.Verify(entry, _encryptor);
            if (!verifyResult.IsValid)
            {
                string incidentReason = verifyResult.FailureReason switch
                {
                    "ENTRY_HASH_MISMATCH" => "HASH_TAMPERED",
                    "ENVELOPE_DIGEST_MISMATCH" => "ENVELOPE_TAMPERED",
                    "DECRYPTION_FAILED" => "CIPHERTEXT_TAMPERED",
                    _ => (verifyResult.FailureReason ?? "PAYLOAD_HASH_TAMPERED")
                };

                await RecordIncidentAndBlockAsync(
                    entry.StreamId,
                    incidentReason,
                    entry.Version,
                    entry.EventId,
                    $"Journal verification failed: {verifyResult.FailureReason}.",
                    now,
                    cancellationToken);
                continue;
            }

            // 3. Sign attestation and update manifest
            string attestationMessage = EcdsaAttestation.BuildAttestationMessageV2(
                _keyManager.KeyId,
                _keyManager.Environment,
                entry.StreamId,
                entry.Version,
                entry.EventId,
                entry.EntryHash,
                now);
            string signature = _keyManager.Sign(attestationMessage);

            var attestation = AttestationRecord.Create(
                entry.StreamId,
                entry.Version,
                entry.EventId,
                entry.EntryHash,
                entry.PayloadHash,
                entry.PreviousHash,
                signature,
                _keyManager.KeyId,
                now);

            await _evidenceRepo.AddAttestationAsync(attestation, cancellationToken);

            if (manifest is null)
            {
                manifest = StreamManifest.Create(entry.StreamId, entry.Version, entry.EntryHash, entry.EventId, now);
                await _evidenceRepo.AddManifestAsync(manifest, cancellationToken);
            }
            else
            {
                manifest.Advance(entry.Version, entry.EntryHash, entry.EventId, now);
            }

            // Update EntityHead(s) in Integrity evidence store
            if (verifyResult.Document?.EntityClaims is not null && verifyResult.Document.EntityClaims.Count > 0)
            {
                foreach (var claim in verifyResult.Document.EntityClaims)
                {
                    var head = await _evidenceRepo.GetEntityHeadAsync(claim.EntityType, claim.EntityId, cancellationToken);
                    if (head is null)
                    {
                        await _evidenceRepo.AddEntityHeadAsync(EntityHeadRecord.Create(
                            claim.EntityType, claim.EntityId, attestation.Id, entry.Version, claim.Revision, entry.EntryHash, claim.Digest, now), cancellationToken);
                    }
                    else
                    {
                        head.Update(attestation.Id, entry.Version, claim.Revision, entry.EntryHash, claim.Digest, now);
                    }
                }
            }
            else
            {
                string snapshotDigest = verifyResult.Document?.Snapshot != null
                    ? OperationalSnapshotEngine.ComputeSnapshotDigest(verifyResult.Document.Snapshot)
                    : entry.PayloadHash;

                var head = await _evidenceRepo.GetEntityHeadAsync(entry.StreamType, entry.StreamId, cancellationToken);
                if (head is null)
                {
                    await _evidenceRepo.AddEntityHeadAsync(EntityHeadRecord.Create(
                        entry.StreamType, entry.StreamId, attestation.Id, entry.Version, 1, entry.EntryHash, snapshotDigest, now), cancellationToken);
                }
                else
                {
                    head.Update(attestation.Id, entry.Version, 1, entry.EntryHash, snapshotDigest, now);
                }
            }

            await _evidenceRepo.SaveChangesAsync(cancellationToken);

            // Update confirmation in Payment DB
            await _paymentRepo.UpdateConfirmationAsync(
                entry.Id,
                signature,
                _keyManager.KeyId,
                now,
                cancellationToken);

            confirmedCount++;
        }

        return confirmedCount;
    }

    private async Task RecordIncidentAndBlockAsync(
        string streamId,
        string incidentType,
        long? version,
        Guid? eventId,
        string details,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var manifest = await _evidenceRepo.GetManifestAsync(streamId, cancellationToken);
        if (manifest is not null)
        {
            manifest.Block(details, now);
        }
        else
        {
            manifest = StreamManifest.Create(streamId, version ?? 1, "BLOCKED", eventId ?? Guid.Empty, now);
            manifest.Block(details, now);
            await _evidenceRepo.AddManifestAsync(manifest, cancellationToken);
        }

        var incident = IntegrityIncident.Create(incidentType, streamId, version, eventId, details, now);
        await _evidenceRepo.AddIncidentAsync(incident, cancellationToken);

        await _evidenceRepo.SaveChangesAsync(cancellationToken);
    }
}
