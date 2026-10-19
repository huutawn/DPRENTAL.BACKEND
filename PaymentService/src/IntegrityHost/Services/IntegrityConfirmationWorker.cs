#pragma warning disable CA1848
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.IntegrityHost.Entities;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityConfirmationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IntegrityKeyManager _keyManager;
    private readonly IOptionsMonitor<IntegrityHostOptions> _options;
    private readonly ILogger<IntegrityConfirmationWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public IntegrityConfirmationWorker(
        IServiceScopeFactory scopeFactory,
        IntegrityKeyManager keyManager,
        IOptionsMonitor<IntegrityHostOptions> options,
        ILogger<IntegrityConfirmationWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntegrityConfirmationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int confirmedCount = await ProcessConfirmationBatchAsync(stoppingToken);
                if (confirmedCount == 0)
                {
                    int delayMs = _options.CurrentValue.ConfirmationIntervalMs > 0
                        ? _options.CurrentValue.ConfirmationIntervalMs
                        : 250;
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing integrity confirmation batch.");
                await Task.Delay(TimeSpan.FromMilliseconds(1000), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("IntegrityConfirmationWorker stopped.");
    }

    public async Task<int> ProcessConfirmationBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var integrityDb = scope.ServiceProvider.GetRequiredService<IntegrityDbContext>();
        var paymentRepo = scope.ServiceProvider.GetRequiredService<ITransactionJournalRepository>();

        int batchSize = _options.CurrentValue.BatchSize > 0 ? _options.CurrentValue.BatchSize : 100;
        var pendingEntries = await paymentRepo.GetPendingConfirmationEntriesAsync(batchSize, cancellationToken);

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

            var manifest = await integrityDb.Manifests
                .FirstOrDefaultAsync(m => m.StreamId == entry.StreamId, cancellationToken);

            if (manifest?.IsBlocked == true)
            {
                _logger.LogWarning("Skipping entry {EntryId} because stream {StreamId} is blocked: {Reason}",
                    entry.Id, entry.StreamId, manifest.BlockedReason);
                continue;
            }

            // 1. Verify sequence order
            if (manifest is null)
            {
                if (entry.Version != 1 || !string.Equals(entry.PreviousHash, CanonicalJournalHasher.GenesisHash, StringComparison.OrdinalIgnoreCase))
                {
                    await RecordIncidentAndBlockAsync(
                        integrityDb,
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
                    var existingAttestation = await integrityDb.Attestations
                        .FirstOrDefaultAsync(a => a.StreamId == entry.StreamId && a.Version == entry.Version, cancellationToken);

                    if (existingAttestation is not null)
                    {
                        if (string.Equals(existingAttestation.EntryHash, entry.EntryHash, StringComparison.OrdinalIgnoreCase))
                        {
                            // Replay existing attestation
                            await paymentRepo.UpdateConfirmationAsync(
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
                                integrityDb,
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
                        integrityDb,
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
                        integrityDb,
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

            // 2. Verify CanonicalPayload and EntryHash computation
            if (!string.IsNullOrWhiteSpace(entry.CanonicalPayload))
            {
                string expectedPayloadHash = CanonicalJournalCodec.ComputePayloadHash(entry.CanonicalPayload);
                if (!string.Equals(entry.PayloadHash, expectedPayloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    await RecordIncidentAndBlockAsync(
                        integrityDb,
                        entry.StreamId,
                        "PAYLOAD_HASH_TAMPERED",
                        entry.Version,
                        entry.EventId,
                        $"PayloadHash {entry.PayloadHash} does not match computed hash from canonical payload {expectedPayloadHash}.",
                        now,
                        cancellationToken);
                    continue;
                }
            }

            string envelopeDigest = CanonicalJournalCodec.ComputeEnvelopeDigest(
                entry.EncryptedPayload, entry.PayloadNonce, entry.PayloadTag, entry.EncryptionKeyId);

            string expectedEntryHash = CanonicalJournalCodec.ComputeEntryHash(
                entry.StreamId,
                entry.Version,
                entry.EventId,
                entry.PreviousHash,
                entry.PayloadHash,
                envelopeDigest);
            if (!string.Equals(entry.EntryHash, expectedEntryHash, StringComparison.OrdinalIgnoreCase))
            {
                await RecordIncidentAndBlockAsync(
                    integrityDb,
                    entry.StreamId,
                    "HASH_TAMPERED",
                    entry.Version,
                    entry.EventId,
                    $"EntryHash {entry.EntryHash} does not match expected {expectedEntryHash}.",
                    now,
                    cancellationToken);
                continue;
            }

            // 3. Sign attestation and update manifest
            string attestationMessage = EcdsaAttestation.BuildAttestationMessage(
                _keyManager.KeyId,
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

            integrityDb.Attestations.Add(attestation);

            if (manifest is null)
            {
                manifest = StreamManifest.Create(entry.StreamId, entry.Version, entry.EntryHash, entry.EventId, now);
                integrityDb.Manifests.Add(manifest);
            }
            else
            {
                manifest.Advance(entry.Version, entry.EntryHash, entry.EventId, now);
            }

            // Update EntityHead in Integrity evidence store
            var head = await integrityDb.EntityHeads
                .FirstOrDefaultAsync(h => h.EntityType == entry.StreamType && h.EntityId == entry.StreamId, cancellationToken);
            if (head is null)
            {
                integrityDb.EntityHeads.Add(EntityHeadRecord.Create(
                    entry.StreamType, entry.StreamId, attestation.Id, entry.Version, entry.EntryHash, entry.PayloadHash, now));
            }
            else
            {
                head.Update(attestation.Id, entry.Version, entry.EntryHash, entry.PayloadHash, now);
            }

            await integrityDb.SaveChangesAsync(cancellationToken);

            // Update confirmation in Payment DB
            await paymentRepo.UpdateConfirmationAsync(
                entry.Id,
                signature,
                _keyManager.KeyId,
                now,
                cancellationToken);

            confirmedCount++;
        }

        return confirmedCount;
    }

    private static async Task RecordIncidentAndBlockAsync(
        IntegrityDbContext db,
        string streamId,
        string incidentType,
        long? version,
        Guid? eventId,
        string details,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var manifest = await db.Manifests.FindAsync([streamId], cancellationToken);
        if (manifest is not null)
        {
            manifest.Block(details, now);
        }
        else
        {
            manifest = StreamManifest.Create(streamId, version ?? 1, "BLOCKED", eventId ?? Guid.Empty, now);
            manifest.Block(details, now);
            db.Manifests.Add(manifest);
        }

        var incident = IntegrityIncident.Create(incidentType, streamId, version, eventId, details, now);
        db.Incidents.Add(incident);

        await db.SaveChangesAsync(cancellationToken);
    }
}
